param([string]$Distro='Ubuntu', [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$CtlDirectory, [ValidateSet('in-process','server')][string]$SessionHost='in-process')
$ErrorActionPreference='Stop'
if (& "$PSScriptRoot/sandbox-ui-entry.ps1" -Test WslCapabilities -Parameters $PSBoundParameters) { exit $LASTEXITCODE }
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$id='agwinterm-capabilities-'+[guid]::NewGuid().ToString('N')
$state=Join-Path $env:LOCALAPPDATA $id
$evidence=Join-Path $repo "artifacts/$id quote's"
$null=New-Item -ItemType Directory -Path $state,$evidence
$ctl=Join-Path $CtlDirectory 'agwintermctl.exe'
$ui=$null
function Ctl([string[]]$Arguments) {
    $raw=(& $ctl @Arguments --pipe $id --json) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Control failed: $raw" }
    $reply=$raw|ConvertFrom-Json
    if (-not $reply.ok) { throw "Control refused: $raw" }
    return $reply.result
}
# Bash, stty and ncurses are test tools already in the distribution, not shipped dependencies.
$probe=@'
set -eu
out=$1
expected=$2
exec 3>"$out"
printf 'TERM=%s\nTERM_PROGRAM=%s\nCOLORTERM=%s\n' "$TERM" "${TERM_PROGRAM-}" "${COLORTERM-}" >&3
test "$TERM" = "$expected"
if test "${3-}" != ssh; then
  test "$TERM_PROGRAM" = agwinterm
  test "$COLORTERM" = truecolor
fi
infocmp -x "$TERM" >&3
test "$(tput colors)" = 256
saved=$(stty -g)
trap 'stty "$saved"' EXIT
stty raw -echo
printf '\033[c'
IFS= read -r -t 5 -d c reply
test "$reply" = $'\033[?62;22'
printf 'DA1=PASS\n' >&3
printf '\033P+q524742\033\\'
IFS= read -r -t 5 -d '\' reply
test "$reply" = $'\033P1+r524742=38\033'
printf 'RGB=PASS\n' >&3
printf '\033P+q626C696E6B;544E\033\\'
if IFS= read -r -t 1 -n 1 reply; then exit 13; fi
printf 'FILTER=PASS\nDONE\n' >&3
sleep 15
'@
$scriptPath=Join-Path $evidence 'probe.sh'
[IO.File]::WriteAllText($scriptPath,$probe.Replace("`r`n","`n"),[Text.UTF8Encoding]::new($false))
$sshProbe=@'
set -eu
root=$1
exec 2>"$root/ssh-error.txt"
work=$(mktemp -d /tmp/agwinterm-ssh-capabilities.XXXXXX)
server=
trap 'test -z "$server" || kill "$server" 2>/dev/null || true; test ! -f "$work/server.log" || cp "$work/server.log" "$root/sshd.log"; rm -rf -- "$work"' EXIT
ssh-keygen -q -t ed25519 -N '' -f "$work/host"
ssh-keygen -q -t ed25519 -N '' -f "$work/client"
cat >"$work/config" <<EOF
ListenAddress 127.0.0.1
HostKey $work/host
PidFile $work/pid
AuthorizedKeysFile $work/client.pub
# The public key is in a private mktemp directory beneath shared /tmp.
StrictModes no
PasswordAuthentication no
KbdInteractiveAuthentication no
UsePAM no
PermitRootLogin prohibit-password
AllowUsers $(id -un)
AllowTcpForwarding no
X11Forwarding no
PermitTunnel no
EOF
for attempt in {1..20}; do
  port=$((22000 + RANDOM % 5000))
  /usr/sbin/sshd -D -e -p "$port" -f "$work/config" >"$work/server.log" 2>&1 &
  server=$!
  sleep 0.2
  if kill -0 "$server" 2>/dev/null; then break; fi
done
read -r kind key rest <"$work/host.pub"
printf '[127.0.0.1]:%s %s %s\n' "$port" "$kind" "$key" >"$work/known_hosts"
kill -0 "$server"
# Like CommandInfo(terminal:true), set TERM on the SSH client; sshd receives it
# through the PTY request. Do not fake TERM on the remote command or use AcceptEnv.
quote() {
  local value=${1//\'/\'\\\'\'}
  printf "'%s'" "$value"
}
remote="bash --noprofile $(quote "$root/probe.sh") $(quote "$root/ssh.txt") xterm-256color ssh"
TERM=xterm-256color ssh -tt -p "$port" -i "$work/client" -F /dev/null \
  -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=yes \
  -o UserKnownHostsFile="$work/known_hosts" -o GlobalKnownHostsFile=/dev/null \
  "$(id -un)@127.0.0.1" "$remote"
'@
[IO.File]::WriteAllText((Join-Path $evidence 'ssh.sh'),$sshProbe.Replace("`r`n","`n"),[Text.UTF8Encoding]::new($false))
$linuxRoot=(& wsl -d $Distro --exec wslpath -u $evidence).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve fixture path in WSL' }
$profiles=@()
foreach ($entry in @(@('default','xterm-256color'),@('override','screen-256color'))) {
    $profile=@{name=$entry[0];command='wsl.exe';args=@('-d',$Distro,'--exec','bash','--noprofile',"$linuxRoot/probe.sh","$linuxRoot/$($entry[0]).txt",$entry[1])}
    if ($entry[0] -eq 'override') { $profile.env=@{TERM=$entry[1]} }
    $profiles += $profile
}
$profiles += @{name='ssh';command='wsl.exe';args=@('-d',$Distro,'--exec','bash','--noprofile',"$linuxRoot/ssh.sh",$linuxRoot)}
@{default='default';profiles=$profiles}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $state 'profiles.json')
try {
    # A launching terminal must not leak its identity into newly created panes.
    $env:TERM='stale-parent-terminal'
    $ui=Start-Process (Join-Path $AppDirectory 'Agwinterm.Win32.exe') -ArgumentList @('--app-id',$id,'--pipe',$id,'--no-restore','--default-session-host',$SessionHost) -WindowStyle Hidden -PassThru
    for($i=0;$i -lt 60;$i++) { try { $null=Ctl @('ping'); break } catch { Start-Sleep -Milliseconds 250; if($i -eq 59){throw} } }
    $null=Ctl @('session','new','--profile','override')
    $null=Ctl @('session','new','--profile','ssh')
    foreach($name in @('default','override','ssh')) {
        $file=Join-Path $evidence "$name.txt"
        for($i=0;$i -lt 100;$i++) { if((Test-Path $file) -and (Get-Content $file -Raw).Contains("DONE")){break}; Start-Sleep -Milliseconds 150 }
        if(-not(Test-Path $file) -or -not(Get-Content $file -Raw).Contains("DONE")){throw "WSL capability probe failed: $file"}
        Write-Host "PASS WSL $SessionHost ${name}: environment, installed terminfo, 256 colors, DA1, RGB, renderer filter ($file)"
    }
} finally {
    if($ui -and -not $ui.HasExited) {
        try { foreach($ws in (Ctl @('tree')).workspaces) { foreach($session in $ws.sessions) {
            $null=Ctl @('session','close','--target',$session.id)
        } } } catch {}
        $null=$ui.CloseMainWindow()
        if(-not $ui.WaitForExit(5000)){Stop-Process -Id $ui.Id -Force}
    }
}

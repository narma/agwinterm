"""Execute the account-shell quoting contract with installed shells; test tooling only."""
import shutil
import subprocess

def quote(value):
    return "'" + ''.join("'\\\\'" if c == '\\' else "'\\''" if c == "'" else "'\\!'" if c == '!' else c for c in value) + "'"

values = ["plain", "space here", "a'b", "a\\b", "a'\\b", "$(touch NEVER);`echo bad`!x*?[]", "путь/界"]
tested = []
for name in ('sh', 'zsh', 'fish', 'tcsh'):
    shell = shutil.which(name)
    if not shell:
        print('NOT INSTALLED', name)
        continue
    for value in values:
        result = subprocess.run([shell, '-c', "/usr/bin/printf '%s' " + quote(value)], capture_output=True, timeout=5)
        assert result.returncode == 0 and result.stdout.decode() == value, (name, value, result)
    tested.append(name)
    print('PASS account shell', name)
assert tested

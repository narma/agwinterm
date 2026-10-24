import json
import os
from pathlib import Path
import shlex
import subprocess
import sys

kind, action, windows_cli, skill, hook, shell = json.load(sys.stdin)
home = Path.home()
data = home / '.local/share/agwinterm'
bin_dir = home / '.local/bin'
claude_dir = Path(os.environ.get('CLAUDE_CONFIG_DIR', str(home / '.claude')))


def parse_config(text, jsonc=False):
    if not jsonc:
        return json.loads(text)
    # Strip comments outside JSON strings, then trailing commas. Keep a backup before rewriting.
    cleaned, i = [], 0
    while i < len(text):
        if text[i] == '"':
            start = i
            i += 1
            while i < len(text):
                if text[i] == '\\':
                    i += 2
                elif text[i] == '"':
                    i += 1
                    break
                else:
                    i += 1
            cleaned.append(text[start:i])
        elif text.startswith('//', i):
            end = text.find('\n', i)
            i = len(text) if end < 0 else end
            cleaned.append(' ')
        elif text.startswith('/*', i):
            end = text.find('*/', i + 2)
            if end < 0:
                raise ValueError('Unclosed JSONC comment')
            i = end + 2
            cleaned.append(' ')
        else:
            cleaned.append(text[i])
            i += 1
    # Strings are single tokens; comma characters within them are never removed.
    for i, token in enumerate(cleaned):
        if token == ',':
            next_token = next((t for t in cleaned[i + 1:] if not t.isspace()), '')
            if next_token in ('}', ']'):
                cleaned[i] = ''
    return json.loads(''.join(cleaned))


def write(path, text, executable=False):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding='utf-8')
    if executable:
        path.chmod(0o700)


def block(path, name, text):
    begin, end = '# >>> agwinterm ' + name + ' >>>', '# <<< agwinterm ' + name + ' <<<'
    old = path.read_text(encoding='utf-8') if path.exists() else ''
    start = old.find(begin)
    if start >= 0:
        finish = old.find(end, start)
        if finish < 0:
            raise ValueError('Incomplete agwinterm block in ' + str(path))
        old = old[:start] + old[finish + len(end):].lstrip('\n')
    write(path, old.rstrip('\n') + '\n' + (begin + '\n' + text + '\n' + end + '\n' if text else ''))


def cli_install():
    # wslpath respects nonstandard automount roots. No assumption about /mnt/c.
    cli = subprocess.check_output(['wslpath', '-u', windows_cli], text=True).strip()
    if not Path(cli).is_file():
        raise FileNotFoundError('agwintermctl.exe is missing: ' + cli)
    write(bin_dir / 'agwintermctl', '#!/bin/sh\nexec ' + shlex.quote(cli) + ' "$@"\n', True)
    for rc in (home / '.profile', home / '.bashrc', Path(os.environ.get('ZDOTDIR', str(home))) / '.zshrc'):
        block(rc, 'cli', 'export PATH="$HOME/.local/bin:$PATH"')


if kind == 'cli':
    if action == 'remove':
        wrapper = bin_dir / 'agwintermctl'
        if wrapper.exists():
            # Only remove our own wrapper.
            if 'agwintermctl.exe' not in wrapper.read_text(encoding='utf-8'):
                raise ValueError('Refusing to remove an unrelated agwintermctl')
            wrapper.unlink()
        for rc in (home / '.profile', home / '.bashrc', Path(os.environ.get('ZDOTDIR', str(home))) / '.zshrc'):
            if rc.exists():
                block(rc, 'cli', '')
    else:
        cli_install()
elif kind == 'skill':
    for base in (claude_dir, Path(os.environ.get('CODEX_HOME', str(home / '.codex')))):
        write(base / 'skills/agwinterm/SKILL.md', skill + '\n\n## WSL\nUse agwintermctl from ~/.local/bin. Local file arguments consumed by the Windows CLI need `wslpath -w`. Linux processes use Linux PIDs. Install integrations from the WSL pane.\n')
elif kind == 'shell':
    write(data / 'shell.sh', shell)
    source = '[ -r "$HOME/.local/share/agwinterm/shell.sh" ] && . "$HOME/.local/share/agwinterm/shell.sh"'
    for rc in (home / '.bashrc', Path(os.environ.get('ZDOTDIR', str(home))) / '.zshrc'):
        block(rc, 'shell', source)
elif kind == 'hooks':
    cli_install()
    write(data / 'hook.py', hook, True)
    common = [('UserPromptSubmit', 'active'), ('PostToolUse', 'active'), ('Stop', 'stop'), ('PermissionRequest', 'blocked'), ('SessionStart', 'bind')]
    for agent, path in (
        ('claude', claude_dir / 'settings.json'),
        ('codex', Path(os.environ.get('CODEX_HOME', str(home / '.codex'))) / 'hooks.json'),
        ('devin', Path(os.environ.get('XDG_CONFIG_HOME', str(home / '.config'))) / 'devin/config.json'),
    ):
        try:
            old = path.read_text(encoding='utf-8') if path.exists() else None
            config = parse_config(old, jsonc=agent == 'devin') if old is not None else {}
            if not isinstance(config, dict):
                raise ValueError('Expected an object')
            hooks = config.setdefault('hooks', {})
            if not isinstance(hooks, dict):
                raise ValueError('Expected hooks object')
            events = common + ([('Notification', 'blocked')] if agent == 'claude' else [])
            for event, state in events:
                command = 'python3 ' + shlex.quote(str(data / 'hook.py')) + ' ' + agent + ' ' + state
                entries = hooks.setdefault(event, [])
                if not isinstance(entries, list):
                    raise ValueError('Expected hook array')
                if any(command == h.get('command') for e in entries if isinstance(e, dict) for h in e.get('hooks', []) if isinstance(h, dict)):
                    continue
                entry = {'hooks': [{'type': 'command', 'command': command}]}
                if event == 'Notification':
                    entry['matcher'] = 'permission_prompt'
                entries.append(entry)
            merged = json.dumps(config, ensure_ascii=False, indent=2) + '\n'
            if old is not None and old != merged:
                backup = path.with_name(path.name + '.agwinterm.bak')
                n = 1
                while backup.exists():
                    n += 1
                    backup = path.with_name(path.name + '.agwinterm-' + str(n) + '.bak')
                write(backup, old)
            write(path, merged)
            print(agent + ': ' + str(path))
        except (ValueError, OSError) as error:
            print(agent + ': refused; existing config untouched: ' + str(error))
    print('Codex: approve the installed hooks in /hooks.')
else:
    raise ValueError('Unknown integration: ' + kind)
print('WSL ' + os.environ.get('WSL_DISTRO_NAME', '') + ': ' + kind + ' ' + action + ' complete; restart the shell.')

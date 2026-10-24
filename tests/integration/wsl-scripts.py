"""Run in WSL: python3 tests/integration/wsl-scripts.py. Uses only a disposable HOME."""
import contextlib
import io
import json
import os
from pathlib import Path
import runpy
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[2] / 'src/Agwinterm.Pty/Wsl'


class WslScripts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='agwinterm-wsl-')
        self.addCleanup(self.temp.cleanup)
        self.home = Path(self.temp.name)
        self.cli = self.home / 'Windows CLI with spaces' / 'agwintermctl.exe'
        self.cli.parent.mkdir()
        self.cli.write_text('#!/bin/sh\nprintf "%s\\n" "$@"\n')
        self.cli.chmod(0o700)
        self.environment = patch.dict(os.environ, {'HOME': str(self.home), 'CODEX_HOME': str(self.home / '.codex'),
            'XDG_CONFIG_HOME': str(self.home / '.config'), 'ZDOTDIR': str(self.home), 'AGWINTERM_ENABLED': '1',
            'CLAUDE_CONFIG_DIR': str(self.home / '.claude')})
        self.environment.start()
        self.addCleanup(self.environment.stop)

    def install(self, kind, action='install', native=False):
        payload = [kind, action, r'C:\Windows CLI with spaces\agwintermctl.exe', 'test skill',
                   (SCRIPTS / 'hook.py').read_text(), (SCRIPTS / 'shell.sh').read_text()]
        if native:
            payload += ['bridge', 'linux']
        paths = {'bridge': str(self.cli.with_name('agwinterm-hook.exe')), 'linux': str(self.cli.with_name('agwinterm-hook-linux-x64'))}
        with patch('sys.stdin', io.StringIO(json.dumps(payload))), patch('subprocess.check_output', side_effect=lambda args, **kwargs: paths.get(args[-1], str(self.cli))), contextlib.redirect_stdout(io.StringIO()) as output:
            runpy.run_path(str(SCRIPTS / 'install.py'), run_name='__main__')
        return output.getvalue()

    def test_native_upgrade_and_fallback_preserve_user_hooks(self):
        self.install('hooks')
        path = self.home / '.codex/hooks.json'
        settings = json.loads(path.read_text())
        settings['hooks']['Stop'][0]['hooks'].append({'command': 'echo mine'})
        path.write_text(json.dumps(settings))
        self.cli.with_name('agwinterm-hook.exe').write_bytes(b'bridge')
        binary = self.cli.with_name('agwinterm-hook-linux-x64')
        binary.write_bytes(b'native')
        self.assertIn('native Rust', self.install('hooks', native=True))
        first = path.read_text()
        self.assertNotIn('python3', first)
        self.assertIn('echo mine', first)
        self.assertEqual(1, len(json.loads(first)['hooks']['Stop']))
        self.install('hooks', native=True)
        self.assertEqual(first, path.read_text())
        installed = self.home / '.local/share/agwinterm/agwinterm-hook'
        self.assertEqual(b'native', installed.read_bytes())
        self.assertEqual(0o700, installed.stat().st_mode & 0o777)
        binary.unlink()
        self.assertIn('Python', self.install('hooks', native=True))
        self.assertIn('python3', path.read_text())
        self.assertIn('echo mine', path.read_text())

    def test_repeat_install_preserves_hooks_and_profile_and_cli_quotes(self):
        settings = self.home / '.claude/settings.json'
        settings.parent.mkdir()
        settings.write_text(json.dumps({'custom': True, 'hooks': {'Stop': [{'hooks': [{'command': 'echo mine'}]}]}}))
        rc = self.home / '.bashrc'
        rc.write_text('export MY_SETTING=preserved\n')
        self.install('hooks')
        first = settings.read_text()
        self.install('hooks')
        self.assertEqual(first, settings.read_text())
        self.assertTrue(json.loads(first)['custom'])
        self.assertIn('echo mine', first)
        self.assertEqual(1, rc.read_text().count('# >>> agwinterm cli >>>'))
        self.assertIn('MY_SETTING=preserved', rc.read_text())
        wrapper = self.home / '.local/bin/agwintermctl'
        result = subprocess.check_output([str(wrapper), 'a b', "single'quote", '$HOME'], text=True)
        self.assertEqual("a b\nsingle'quote\n$HOME\n", result)
        self.install('cli', 'remove')
        self.assertFalse(wrapper.exists())
        self.assertIn('MY_SETTING=preserved', rc.read_text())

    def test_invalid_settings_not_overwritten(self):
        path = self.home / '.codex/hooks.json'
        path.parent.mkdir()
        path.write_text('{invalid')
        self.assertIn('refused', self.install('hooks'))
        self.assertEqual('{invalid', path.read_text())

    def test_devin_jsonc_is_backed_up_and_preserves_string_contents(self):
        path = self.home / '.config/devin/config.json'
        path.parent.mkdir(parents=True)
        original = '{// user comment\n"url":"https://example.test/a,}", /* comment */ "enabled":true,}'
        path.write_text(original)
        self.install('hooks')
        self.assertEqual('https://example.test/a,}', json.loads(path.read_text())['url'])
        self.assertEqual(original, path.with_name('config.json.agwinterm.bak').read_text())

    def test_bash_and_zsh_osc7_paths_and_existing_prompt(self):
        self.install('shell')
        self.install('shell')
        cwd = self.home / "space % тест ' quote"
        cwd.mkdir()
        for shell in ('bash', 'zsh'):
            script = '. "$HOME/.local/share/agwinterm/shell.sh"; __agw_prompt'
            result = subprocess.check_output([shell, '-c', script], cwd=cwd)
            self.assertTrue(result.startswith(b'\x1b]7;file://localhost/'), (shell, result))
            self.assertIn(b'space%20%25%20%D1%82%D0%B5%D1%81%D1%82%20%27%20quote', result, shell)
            self.assertTrue(result.endswith(b'\x07'))
        result = subprocess.check_output(['bash', '-c', 'PROMPT_COMMAND=("echo existing"); . "$HOME/.local/share/agwinterm/shell.sh"; . "$HOME/.local/share/agwinterm/shell.sh"; declare -p PROMPT_COMMAND'], text=True)
        self.assertIn('echo existing', result)
        self.assertEqual(1, result.count('__agw_prompt'))

    def test_skills_respect_codex_home(self):
        with patch.dict(os.environ, {'CODEX_HOME': str(self.home / 'custom codex')}):
            self.install('skill')
        self.assertTrue((self.home / 'custom codex/skills/agwinterm/SKILL.md').exists())

    def test_ancestry_rejects_pid_from_another_pane(self):
        result = subprocess.run(['python3', str(SCRIPTS / 'ancestry.py'), str(os.getpid()), 'not-this-pane'], capture_output=True)
        self.assertNotEqual(0, result.returncode)


if __name__ == '__main__':
    unittest.main()

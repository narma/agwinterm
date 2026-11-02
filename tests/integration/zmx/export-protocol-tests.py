"""Host-free endpoint provisioning/relay checks with an explicitly fake control executable."""
import hashlib
import json
import os
from pathlib import Path
import pwd
import subprocess
import sys
import tempfile
import unittest

helper=str(Path(sys.argv.pop(1)).resolve())

class ExportProtocolTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='aw-export-protocol-')
        self.root=Path(self.temp.name)
        self.binary=self.root/'zmx'
        self.binary.write_text('#!/bin/sh\nexit 99\n')
        self.binary.chmod(0o700)
        self.root.joinpath('zmx.agwinterm-capabilities.json').write_text(json.dumps({
            'version':1,'sha256':hashlib.sha256(self.binary.read_bytes()).hexdigest(),
            'capabilities':['managed-leadership','authoritative-screen','acknowledged-input','atomic-publication',
                            'snapshot-replay','existing-fd-attach','primary-history-replay']}))
        self.socket=self.root/'s'; self.socket.mkdir(mode=0o700)
        self.cli=self.root/'fake-control'
        self.cli.write_text('#!/bin/sh\nprintf \'{"ok":true,"result":{"windows":[]}}\\n\'\n')
        self.cli.chmod(0o700)
        self.config=dict(distribution=os.environ.get('WSL_DISTRO_NAME','test'),user=pwd.getpwuid(os.getuid()).pw_name,
                         origin='test',executable=str(self.binary),socketDirectory=str(self.socket),cli=str(self.cli),pipe='test')
        self.endpoint=self.root/'export'

    def tearDown(self): self.temp.cleanup()

    def invoke(self,args,raw=None,ok=True):
        p=subprocess.run([str(x) for x in args],input=raw,text=True,capture_output=True,timeout=30)
        self.assertEqual(p.returncode,0 if ok else 1,(p.stdout,p.stderr))
        value=json.loads(p.stdout)
        self.assertEqual(value['ok'],ok)
        return value

    def provision(self,ok=True,raw=None):
        return self.invoke([helper,'--zmx-export-provision',self.endpoint],raw or json.dumps(self.config)+'\n',ok)

    def test_empty_tree_disable_and_unsupported_controls(self):
        self.provision()
        cli=self.endpoint/'agtermctl'
        result=self.invoke([cli,'zmx','tree','--json'])['result']['remote']
        self.assertEqual([],result['sessions'])
        self.assertNotIn('presentation',result)
        self.assertTrue(Path(result['endpoint']['executable']).is_file())
        for args in [('zmx','kill','anything'),('zmx','present','anything'),('session','close'),('zmx','tree','--json','extra')]:
            self.invoke([cli,*args],ok=False)
        self.invoke([helper,'--zmx-export-disable',self.endpoint])
        self.invoke([cli,'zmx','tree','--json'],ok=False)

    def test_disable_survives_missing_dependencies_and_is_idempotent(self):
        self.provision()
        cli=self.endpoint/'agtermctl'
        remote=self.invoke([cli,'zmx','tree','--json'])['result']['remote']
        binary=self.binary.read_bytes()
        self.binary.unlink()
        self.socket.rmdir()
        self.invoke([helper,'--zmx-export-disable',self.endpoint])
        self.invoke([helper,'--zmx-export-disable',self.endpoint])
        self.binary.write_bytes(binary); self.binary.chmod(0o700)
        self.socket.mkdir(mode=0o700)
        self.invoke([cli,'zmx','tree','--json'],ok=False)
        self.invoke([remote['endpoint']['executable'],'attach','missing'],ok=False)

    def test_disable_still_requires_private_matching_authority(self):
        self.provision()
        config=self.endpoint/'config.json'
        original=config.read_text()
        value=json.loads(original); value['uid']=os.getuid()+1
        config.write_text(json.dumps(value))
        self.invoke([helper,'--zmx-export-disable',self.endpoint],ok=False)
        self.assertFalse((self.endpoint/'disabled').exists())
        config.unlink()
        other=self.root/'other'; other.write_text(original); other.chmod(0o600)
        config.symlink_to(other)
        self.invoke([helper,'--zmx-export-disable',self.endpoint],ok=False)
        self.assertFalse((self.endpoint/'disabled').exists())

    def test_failed_provisioning_rolls_back_only_new_directory(self):
        self.cli.write_text('#!/bin/sh\nexit 1\n')
        self.provision(ok=False)
        self.assertFalse(self.endpoint.exists())
        self.endpoint.mkdir(mode=0o700)
        marker=self.endpoint/'user-file'; marker.write_text('keep')
        self.provision(ok=False)
        self.assertEqual('keep',marker.read_text())

    def test_duplicate_unknown_oversized_and_incomplete_requests_refuse(self):
        raw=json.dumps(self.config)
        for invalid in [raw[:-1]+',"user":"other"}\n',raw[:-1]+',"unknown":true}\n',raw,' '*65537+'\n','[]\n']:
            self.provision(ok=False,raw=invalid)
            self.assertFalse(self.endpoint.exists())

    def test_missing_ui_bad_output_and_symlinked_state_refuse(self):
        self.provision()
        cli=self.endpoint/'agtermctl'
        for body in ['exit 1','printf \'{"ok":true,"ok":false}\'','printf \'not json\'']:
            self.cli.write_text('#!/bin/sh\n'+body+'\n')
            self.invoke([cli,'zmx','tree','--json'],ok=False)
        config=self.endpoint/'config.json'
        original=config.read_text(); config.unlink()
        other=self.root/'other'; other.write_text(original); other.chmod(0o600)
        config.symlink_to(other)
        self.invoke([cli,'zmx','tree','--json'],ok=False)

unittest.main()

# Previous helper reference

The [ZMX development report](../../../../docs/wsl-zmx-development.md) documents the shipped Rust helper and the recovery checks that use these reference fixtures.

These Python modules are test-only references for the v1 ledger and ZMX ABI.
They are not embedded, installed or invoked by the application. The production
implementation is native/agwinterm-hook/src/zmx and the lifecycle integration
suite invokes that binary. References remain useful to generate legacy records
and independently verify exact process/socket identity during migration tests.

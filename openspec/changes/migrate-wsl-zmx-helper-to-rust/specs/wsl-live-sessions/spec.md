## ADDED Requirements

### Requirement: Live lifecycle needs no Python runtime
Live session creation, inspection, attachment, recovery and termination SHALL work using the bundled native Linux helper without an installed Python interpreter. Missing native payloads SHALL report unavailable without installing dependencies or launching fallback workloads.

#### Scenario: Distribution has no Python
- **WHEN** a supported WSL distribution has the attested ZMX executable but no Python interpreter
- **THEN** live lifecycle operations and workload survival after Windows host loss remain supported

#### Scenario: Native payload missing
- **WHEN** the bundled Linux helper for the selected architecture is missing
- **THEN** live creation fails visibly without installing dependencies or running a fallback shell

#### Scenario: Recover a previous helper checkpoint
- **WHEN** a saved live binding refers to the prior script helper
- **THEN** recovery uses the native helper at the same endpoint, retains exact identity and operation evidence, and does not rerun the workload

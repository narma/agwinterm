# Stage-4 wire capture

Captured on 2026-11-02 from an isolated macOS arm64 agterm application and its own bundled CLI at `c63e5158e637662ded7ba3a12eac4881b24caef1`. Final signed binary hashes are in `provenance.json`; ZMX is the unchanged stage-2 existing-only patched pair. The installed application was not replaced.

`pinned-frames.json` records actual origin output and viewer input accepted by `zmx present` and `session overlay page-serve`. The question answer elicited `ask.dismiss`; the file resource produced head/chunk/end. Program and URL requests were explicitly refused by the fixture without executing a program or navigating a URL. The capture is protocol evidence, not full Agwinterm interoperability acceptance.

Sanitization: consistent UUID substitution, isolated root renamed, single-use page tokens redacted, JSON whitespace normalized. Scalar encodings and omitted fields remain unchanged. File source uses `{"file":{"path":"...","grantRoot":"..."}}`; URL source uses `{"url":{"_0":"..."}}`. Page chunks are base64. `ask.owner`, frame `gen`, and frame `rev` are separate counters.

Pinned source caps: `PresentationCodec.maxFrameBytes` = 262144 (encoded frame including LF must fit), `maxPendingFrames` = 256; `PageServeCodec.maxFrameBytes` = 262144 and `maxChunkBytes` = 65536. These are source constants, not an assertion that every boundary was exercised by this capture.

Reproduce with `tests/integration/zmx/build-presentation-peer.sh`, `remote-peer-fixture.py`, and `capture-presentation.py`, supplying new isolated roots. Capture explicitly claims only its existing test daemon through the verified stage-2 bridge: upstream interactive delegation additionally requires the origin terminal to be a follower. Acquiring presentation authority alone does not change terminal leadership.

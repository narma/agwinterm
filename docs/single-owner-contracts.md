# Session presentation ownership and threading

`ISession.Presentation` is the optional transition seam. A null value means a
legacy or not-yet-migrated adapter, not permission to construct a fallback core.
`ISessionPresentation` separates capture, semantic input/commands and cancellable
queries. The local implementation calls its owner directly; the hosted adapter
uses the negotiated transport. Neither permits per-cell remote calls during paint.

`TerminalFrame` copies a bounded viewport into owned pooled storage. It has one
publisher reference and any number of explicitly acquired leases. Disposing the
publisher does not invalidate an existing lease. A lease can retain its exact
generation after publication of a replacement; readers must not dispose that
same lease concurrently with reading it. The final reference returns and clears
storage, releasing cell strings and links. No live native pointer or history
replica is retained by a frame.

Frame identity combines session incarnation, attachment epoch, viewport revision,
publication sequence and terminal generation. A frame cache holds one latest
complete frame and rejects old epochs/revisions/sequences. A reset advances the
attachment epoch for the same incarnation. Decoders validate sizes and delta base
identity before allocating or publishing; a partial frame is never visible.
The renderer's extra lease protects its submitted generation while another frame
arrives. Capture begins under the owner mutation lock and completes outside it,
with a separate capture gate preventing concurrent use of native capture storage.

Windows translation, IME and physical key target tracking stay on the UI thread.
Semantic input is encoded against owner modes. Raw input is a distinct operation
whose bytes are never encoded again. Accepted operations preserve order; closed,
overflow and uncertain delivery are explicit outcomes. Uncertain bytes are never
replayed after reconnect. Terminal reply callbacks only enqueue while feed holds
the mutation lock. The ordered writer releases that lock before PTY IO. A
dedicated input reader can claim the same writer when idle; it cannot reorder
around previously queued replies or user input.

The input channel is capped at 4,096 queued records and 1 MiB of queued bytes;
one in-flight record/batch is additional and bounded. Immediate UI submission
starts overlapped Windows IO but never waits for completion. The dedicated writer
owns pending completion and pinned-buffer lifetime. Only a persistent UI thread
may use that optimization; transient/control callers queue normally. Cancellation
must finish the kernel operation before freeing its buffer, OVERLAPPED or pipe.

Queries carry incarnation, attachment epoch and terminal generation, plus bounded
row/result limits and cancellation. Results repeat these fences. Adapters must
reject stale results and cancellation after replacement/close. A query must not
hold the mutation lock while performing long conversion, searching or transport.
Viewport reads may use a retained immutable lease; history/search use bounded owner
captures. These contracts do not imply that all query implementations already
exist: remaining consumer migration is tracked in
`single-owner-consumer-checklist.md` and the OpenSpec tasks.

The presentation publisher has one frame credit. Output coalesces a wakeup while
credit is exhausted; successful EndDraw releases the next credit. Explicit control
readback also consumes the cached frame and releases its credit, allowing later
reads to progress when a hidden/occluded window never paints. This does not request
a paint or enter the ordinary keyboard/render timing path. The publisher never holds
the feed lock during a pipe write, and has no fixed repaint timer. Synchronized
output uses its explicit deadline. T8 is application EndDraw submission, not a
physical display timestamp.

A hosted display write is an ordered command to the owner. Its successful reply
means the command was queued; a subsequent cached viewport read may still show
the preceding frame. Clients requiring read-after-write visibility should wait
for the expected text or frame. Explicit reads release credit even without a
paint. After child exit the same attachment still serves history, selection and
resize; ordinary terminal input is closed.

# RyuSync SSBU sessions

Both players need this version of RyuSync (protocol 3) and **SSBU 1.0.0**. On each computer, configure
the physical controller as local Player 1. RyuSync maps the inviter to shared P1 and
the guest to shared P2.

In the RyuSync window, the host can choose **Frames to buffer** from 0 to 8 before
inviting (default 3). The guest adopts the host's value. The control is locked
while connected; disconnect to change it. The value lasts for this app session.
More buffering gives inputs longer to arrive, at the cost of extra delay.
The buffer counts numbered application updates. At 60 updates per second, each
adds about 16.7 ms; slower play increases that delay. This is experimental and
does not guarantee matching game state.

Connect, click Ready on both computers, then launch SSBU on both. Before either
emulated game starts, the host exports the selected user's committed SSBU account
save and the game's device save. The guest checks the archive's SHA-256 checksum
and prepares a temporary session copy. Both peers must acknowledge preparation
before launching. Transfers time out after three minutes; disconnect and reconnect
to retry. Saves larger than 256 MiB are rejected.

The SSBU version, firmware, language, region, time zone, docked mode and emulated
CPU clock multiplier must match. Physical CPU speeds do not need to match.
DLC and mods must also match; their contents are not yet verified
by the handshake.

The guest's game reads and writes the temporary copy, including after a network
disconnect. Normal game-save writes on the guest never switch back to their own
account/device save. Deleting entire save containers and raw save-storage access
are unsupported in guest sessions. Closing the game removes the temporary copy;
a process crash may leave it in the operating system's temporary directory. The
host uses their normal save and keeps progress. An absent host save produces an
empty session save, not a fallback to the guest's progress.

These are ordinary game saves, **not** CPU/RAM savestates. The session also shares
the initial emulated system time and the application's `csrng` byte stream. Host
cryptography is unchanged; emulated Internet access starts disabled for sessions.

## Cursor drift and determinism

The saved capture `20261023-201224` recorded three controller polls but four
application calls. Both battle counters advanced in each call, including the
extra call without another poll. Debugger pauses can trigger catch-up; this is
evidence of the mechanism, not its frequency in normal play.

For the exact supported main executable, the loader now applies a guarded patch
in memory. It forces the loop's input count to one and always takes the fresh-input
path. The catch-up queue still drains, but its extra application call is removed.
Before SSBU polls controllers, a private SVC waits for the numbered input pair and
commits both controllers once. Two more SVCs check the call/return order and emulate
the instructions they replace. Host time caps speed at 60 updates per second and
paces retries; it does not assign inputs to updates. Missing input yields the
emulated core and logs a warning after five seconds. It does not impose a timeout
on legitimate loading or pauses; closing the game cancels the wait. On failure or
disconnect, the update thread stays stopped until the game is closed. Reconnecting
does not resume that running game. Other workers are not globally frozen by this hook.

The build ID and all affected instructions are checked before patching. Unsupported
executables or conflicting code patches fail launch rather than using the old
clock path. The game file is never modified. Translation disk caching is disabled
for patched sessions to avoid using or saving translations of different code.
Offline launches remain unchanged. Buffer selection stays in the RyuSync window.

This is a prototype, not verified deterministic lockstep. The application loop
covers menu dispatch as well as matches, but the new behavior in menus, transitions,
pause and slow motion still needs live verification. Forcing the ordinary path and
removing extra calls can change timing-dependent behavior. No comparison of game
memory verifies that the two simulations still agree.

Matching a seed does not synchronize CPU scheduling, timer reads, random-call
ordering, asynchronous services, or game-internal RNGs. Cursor and gameplay drift
can persist with this update. Re-aligning cursors at a screen edge only hides one
visible symptom; it does not repair the rest of the game state.

A complete fix needs simulation-frame lockstep with inputs consumed at equivalent
emulated points, controlled timing/randomness, and state checks to detect divergence
(plus resynchronization or rollback). Simply tying input to display refresh or
copying a save does not establish those guarantees, especially across Windows x64
and macOS arm64.

The hook calls `TryGetInputsForSimulationStep`. It consumes one pair per numbered
step, latches repeated requests and leaves a missing pair pending. It rejects
skipped steps and mixing step requests with the computer-clock path. Protocol 4
rejects older peers; both sides must use this behavior.

### Battle readiness (experimental)

The paired recordings showed identical inputs but unequal battle advancement,
starting before the reported hit mismatch. The hook now checks readiness before
SSBU 1.0.0 advances its battle clock and dispatches the full battle phases.
Both peers publish a vote for the current numbered application update. If one
finishes that update without reaching full battle processing, it publishes a
negative vote. The other skips clock advancement and the full phase dispatch for
that update. Loading, restricted transition processing, and the original
post-battle callback continue. This avoids waiting for a battle callback that a
loading peer cannot reach. Inputs remain paired by the existing update number.

When both reach full processing, their six observed battle-clock words must
match before proceeding. A mismatch stops the session and records both values in
the diagnostic error. No counters are overwritten and no existing desync is
repaired. Restart the game after a failure; export both diagnostics first.

This version exchanges a small TCP vote per application update and waits for
the peer during full battle processing. TCP sends immediately rather than
batching small packets. Network latency can therefore reduce throughput even
with buffered inputs. This deliberately prioritizes checking battle progression;
it is not the final low-latency design. It still needs two-PC gameplay validation,
including loading, rematches, pause, and slow-motion effects. Equal clock words
do not establish equal fighter state or deterministic worker execution.

## Verification

### Capturing gameplay divergence

The diagnostic build keeps the last 8192 completed application updates in memory
(about 137 seconds at 60 updates/sec). It records the actual P1/P2 input pair and
six battle-clock words immediately before and after the existing application call.
The clock addresses are verified against the supported executable. No additional
breakpoints, game-memory writes, network messages or per-update disk writes are used
by diagnostic recording itself; the battle readiness gate separately sends votes.
Small read/record overhead remains; this is not a claim of zero timing impact.

Reproduce with Zelda versus Ganondorf on both PCs. Prefer the same named stage and
rules on both. As soon as a hit differs, both players should open RyuSync and click
**Export recent diagnostics**. Files are saved under Ryujinx's `Logs` directory;
the window displays the full path. Export remains available after stopping the
game, until another session starts or the app closes. Send both files. No game
code, saves or keys are included. A hash of the shared session seed identifies
matching sessions; the seed itself is not exported.

For analysis, run `python tools/compare_ryusync_diagnostics.py host.txt guest.txt`.
The comparison matches update numbers, not wall-clock timestamps. It rejects files
from different sessions/builds or two files from the same role. It reports the
earliest difference **within their retained overlap**, gaps and invalid input
metadata. Older divergence may be outside the retained window.

These observations do not include fighter positions, damage, actions or full RNG
state; those layouts remain unverified. Equal records do not prove equal gameplay.
Different battle clocks narrow the investigation but do not prove the cause.
The reads are on the application thread, not an atomic snapshot of all workers.
Do not force the clocks to agree or overwrite fighter state based only on this
capture. First compare both exports, then target the differing path for a fix.

`dotnet run --project tests/RyuSync.NetworkTests -c Release` exercises real TCP/UDP
input transport, host/guest controller mapping, chunked save transfer, startup
acknowledgements, isolation, cleanup, shared random bytes, incompatible settings,
corrupt archives, unsafe paths and disconnects. It requires no external packages.
It also checks numbered steps with buffer sizes 0–8 for both roles, including
delayed arrival, repeated reads, skipped steps and mixing clock modes.
It also tests atomic patch rejection, instruction encoding, fresh-input routing,
removal of the extra application call and valid/invalid update sequences. The
instruction guards were separately compared with the saved local SSBU excerpts.

After preparing the existing pinned LibHac package as in the build workflow,
`tests/RyuSync.SaveTests` verifies reads, writes, commit and read-only mounts using
the same filesystem implementation as the emulator. Both checks run in Windows
x64 and macOS arm64 CI. They do not replace two-computer SSBU gameplay testing.

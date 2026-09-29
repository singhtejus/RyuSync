# RyuSync SSBU sessions

Both players need this version of RyuSync (protocol 2). On each computer, configure
the physical controller as local Player 1. RyuSync maps the inviter to shared P1 and
the guest to shared P2.

Connect, click Ready on both computers, then launch SSBU on both. Before either
emulated game starts, the host exports the selected user's committed SSBU account
save and the game's device save. The guest checks the archive's SHA-256 checksum
and prepares a temporary session copy. Both peers must acknowledge preparation
before launching. Transfers time out after three minutes; disconnect and reconnect
to retry. Saves larger than 256 MiB are rejected.

The SSBU version, firmware, language, region, time zone, docked mode and CPU clock
rate must match. DLC and mods must also match; their contents are not yet verified
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

Matching starting data is necessary but insufficient for deterministic play.
The current input loop advances at approximately 60 Hz using each host's
`Stopwatch`, independently of the game's actual simulation frames. A controller
state can therefore be observed for different numbers of game updates on the two
machines. HID initialization now populates its ring buffers directly instead of
injecting synthetic neutral states into the netplay input stream. No comparison of game memory verifies
that the two simulations still agree.

Matching a seed does not synchronize CPU scheduling, timer reads, random-call
ordering, asynchronous services, or game-internal RNGs. Cursor and gameplay drift
can persist with this update. Re-aligning cursors at a screen edge only hides one
visible symptom; it does not repair the rest of the game state.

A complete fix needs simulation-frame lockstep with inputs consumed at equivalent
emulated points, controlled timing/randomness, and state checks to detect divergence
(plus resynchronization or rollback). Simply tying input to display refresh or
copying a save does not establish those guarantees, especially across Windows x64
and macOS arm64.

## Verification

`dotnet run --project tests/RyuSync.NetworkTests -c Release` exercises real TCP/UDP
input transport, host/guest controller mapping, chunked save transfer, startup
acknowledgements, isolation, cleanup, shared random bytes, incompatible settings,
corrupt archives, unsafe paths and disconnects. It requires no external packages.

After preparing the existing pinned LibHac package as in the build workflow,
`tests/RyuSync.SaveTests` verifies reads, writes, commit and read-only mounts using
the same filesystem implementation as the emulator. Both checks run in Windows
x64 and macOS arm64 CI. They do not replace two-computer SSBU gameplay testing.

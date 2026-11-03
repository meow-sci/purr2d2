# purr2d2

A Kitten Spacy Agency mod to add:

- BFL9K (Big Fucking Laser 9000)
- The Furce

BFL9K's **DROP** button opens a downward spin pill while held. Each press starts
at **0×** spin; halfway is the previous **1×** release rate and the bottom is **2×**.
Release to reach out with an open, palm-down paw and drop the hilt into native
physics. A tap drops with no initial spin. A lit blade stays lit. Hold **UNDROP**
to choose the return time: **1 second** at the top, down to **200 ms** at the bottom.
A tap uses 1 second. Release to fly it back from its current position, easing to
a stop in the paw. Saving, unequipping or disabling
the mod also returns transient dropped equipment to its owner. Blade length can
be set up to **50 m** before character scaling.

Sabers and capes follow the kitten's observed visual scale, including per-axis
scaling, without an unscience dependency. Manual blade contact remains active
while jumping, somersaulting, swimming or underwater. Water does not extinguish
the blade. Ocean views apply water attenuation at the blade's own distance while
solid objects still block it; the glowing blade does not reproduce water's
refraction distortion.

Manual SWING and assisted Slice selection can cut ground rocks and trees into physical
fragments. Tree cuts split the wood and carry the leaves with the resulting pieces,
preserving leaf transparency and two-sided rendering. Unsupported custom geometry
can still fall back to felling the whole tree. Trunk cut ends are solid wood faces
with centered growth rings. Rock cut faces cool from a glow; split
tree ends burn for **10 seconds** by default. Adjust or disable that fire under
**Settings → Clutter Slicing**.

By default, ordinary walking and pose motion do not initiate cuts. Enable
**Settings → Slicing methods → Cut during ordinary movement** to allow them.
That setting also applies to vessel parts. The same section selects contact
detectors, cut-plane strategies, and queued processing for vessels, trees, and
rocks. Queued processing retains accepted cuts after the swing and prepares
geometry on bounded background workers. Cached ribbon, capsule sweep, and
extended clutter coverage are experimental. Extended coverage is off by default;
it warms physical clutter cells asynchronously and reports coverage status.
The actual 50 m forest behavior and game performance still need native testing.

**Slicing Diagnostics** can record timings and capture one confirmed vessel
mesh interval for offline comparison. See [the replay instructions](tools/SliceReplay/README.md).

# media

![bfl9k_01](docs/media/bfl9k_01.webp)

![bfl9k_02](docs/media/bfl9k_02.webp)

![bfl9k_03](docs/media/bfl9k_03.webp)

![blf9k_04](docs/media/blf9k_04.webp)

![slicing_01](docs/media/slicing_01.webp)

![the_furce_01](docs/media/the_furce_01.webp)

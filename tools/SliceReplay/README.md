# Copied mesh replay

Run `dotnet run --project tools/SliceReplay -- tools/SliceReplay/example.json`.
The fixture contains copied triangles and consecutive blade endpoint pairs in one
object-local frame. Each CSV row reports one method's hit time, along-blade
coordinate, triangle tests and BVH node visits against identical input. A hit is a
mesh intersection, not an accepted or completed cut. The cold BVH build cost is
not included.

To record one real vessel contact, open **Slicing Diagnostics** in KSA and press
**Capture next confirmed vessel hit for SliceReplay**. The one-shot capture copies
only the confirmed hit mesh and the endpoint interval that produced that contact;
it records no part names, scene IDs or world coordinates. It accepts at most
40,000 triangles, copies at most 64 primitive arrays and 2,048 triangles per
settled update with a 0.25 ms soft time budget, and writes an atomic JSON file
capped at 16 MiB. The file is saved as `slice-replay-capture.json` beside the mod's
settings file. Run the tool against that file to compare mesh algorithms offline.

Capture is disabled until explicitly armed. It is observational: it does not
change contact admission, cut geometry or commit behavior. If the hit mesh changes
or leaves the scene during copying, capture aborts without writing a partial file.
The legacy/cached rows replay the same target-local mesh and one observed endpoint
ribbon; runtime metadata reports detector, evidence completeness and the hit
coordinates for context.

Runtime capture is opt-in, but its copied geometry can still contain content from
the selected game asset. Keep the fixture local unless you intend to share it.
The tool caps input file size, triangles, intervals and update iterations. A
runtime fixture covers one hit mesh and one interval only: it does not measure
whole-vessel discovery, multiple subparts, native clutter proxies, real frame
time, physical coverage, queue throughput or final cut success. Native proxy
comparisons require a dedicated native fixture/world reset.

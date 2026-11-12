A trimmed-down continuation of [PicaVoxel](https://github.com/GarethIW/PicaVoxel) with support for infinite streamed voxel volumes. Think Minecraft terrain.

No docs as yet, but there's a demo scene included with a full setup for terrain with a runtime editor (manipulator) and disk-based persistence. There's also support for volume manipulation and voxel detection via Visual Scripting.

Can be installed as a Github Unity package: https://github.com/GarethIW/PicaVoxelInfinity.git?path=Packages/com.garethiw.picavoxelinfinity

## Performance

Culled meshing (the mode that keeps one quad per block face, so textures tile per block) runs in Burst jobs on Unity's job workers. Collision meshes are the same faces with coplanar ones merged, cooked on a worker and swapped in, so neither meshing nor collider cooking runs on the main thread except for edits, which mesh and cook straight away so the change shows and collides that frame. Greedy and Marching meshing still use the managed mesher on the thread pool.

Volumes start work nearest the camera first, within limits you can tune under **Performance** on the Volume:

- **Max Concurrent Mesh Jobs** / **Max Concurrent Data Jobs**: how many chunks mesh or generate at once (0 picks from the CPU).
- **Mesh Apply Budget Ms** / **Chunk Create Budget Ms**: main-thread time per frame for uploading finished meshes and for creating or recycling chunks (at least one of each a frame).
- **Max Neighbour Wait Frames**: how long a chunk waits for its neighbours to generate before meshing anyway (it meshes once rather than again as each neighbour lands).
- **Neighbour Remesh Delay**: how long a chunk that is already showing a mesh waits before re-meshing for a neighbour's new data, so several arriving together cost one re-mesh.
- **Merge Collider Faces**: merge coplanar collision faces (same surfaces, far fewer triangles).
- **Periodic Remesh**: re-mesh the outer ring of chunks every pass, as older versions did. Off by default; it only costs time now.

Chunk processors that write into neighbouring chunks should use `Volume.WriteVoxelFromNeighbour`, which holds the write until that chunk has generated rather than having the generator overwrite it. `PicaVoxelStats` counts the work volumes do, and `Assets/PerfTests` has a benchmark and equivalence tests (run them from the Test Runner, PlayMode).

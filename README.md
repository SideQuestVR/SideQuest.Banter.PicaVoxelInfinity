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

## Tile array terrain (no shimmer at a distance)

The tile sheet terrain shaders can't use mipmaps (lower mips of a padded sheet bleed between tiles), so distant terrain shimmers, especially on Quest. **PicaVoxel URP Infinite Terrain Tile Array** samples a `Texture2DArray` instead: one slice per tile, with its own mips. Texels stay pixel-sharp up close (nearest texel of the full-size tile, exactly as the tile sheet shader shows them), and distant terrain is filtered. In a test of terrain moving under the camera, frame-to-frame shimmer dropped by about three quarters. It is opaque with alpha cut-out, and at the draw distance it fades into the sky's colour in that direction rather than going transparent, so the edge of the world meets the skybox.

It is separate from the existing shaders, materials and textures, which are unchanged. To use it:

1. **Tools > PicaVoxel > Tile Array Builder**: pick your tile sheet terrain material (it reads the sheet, columns, rows and padding from it) and build. The sheet is only read. Cut-out tiles (leaves) keep the same share of solid texels in every mip, so they don't thin out at a distance.
2. Make a material with the **PicaVoxel/PicaVoxel URP Infinite Terrain Tile Array** shader (or copy *Materials/URP/PicaVoxel URP Tile Array Terrain*, made from the included tile sheet). Set the tile array, the tile sheet (custom block meshes still sample it) and the number of tiles, and assign it to the Volume.
3. Choose where the fade takes the sky's colour from:
   - **Cubemap**: exactly what the skybox shows. Build one with **Tools > PicaVoxel > Skybox Cubemap Builder** from the skybox material (6-sided, cubemap, panoramic or procedural all work); it renders the sky the way the game's camera does.
   - **Reflection**: the scene's reflection probe (the default; it follows the skybox if the environment reflections come from it, but softer).
   - **Color**: one fade colour.

**Smooth Between Mips** blends between mip levels at a distance (on by default); off, each distance uses the nearest mip, which is cheaper still but shows faint bands where the mip changes. **Mip Bias** moves the switch-over: positive goes soft sooner, negative stays sharp further out. `Assets/PerfTests/Editor` has the tests for all of this (Test Runner, EditMode).

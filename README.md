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
- **Cave Culling**: infinite volumes with Culled meshing don't draw chunks the camera can't see through open space (see below). On by default.
- **Cull World Floor**: infinite volumes with Y bounds don't make the faces on the underside of the bottom row of chunks. On by default.

### Cave culling

Underground chunks are drawn even though you can never see them: tunnels and caverns add faces, and nothing hides terrain behind other terrain. Cave culling skips them, the way Minecraft does:

1. When a chunk meshes (in the same Burst job), its open voxels (air, and see-through blocks such as leaves, glass and water) are flood filled, and the chunk records which pairs of its six sides are joined by open space: a 15 bit mask, 0 for solid rock, all 15 for open air.
2. A second small Burst job walks outwards from the camera's chunk, moving from chunk to chunk only through sides its mask says are joined, and never turning back along an axis. It runs when the camera changes chunk or a chunk's mask changes, over a grid of the chunks near the camera.
3. Chunks the walk doesn't reach have their renderers switched off. Their colliders stay on, and with shadows on they still draw into the shadow maps (shadows only).

It never hides something the camera can see (the tests check rays against it); it can keep chunks that are actually hidden behind solid ground, since it follows connections rather than looking at where things are. Chunks without a mask yet count as open. In a cave the surface mostly disappears instead. A chunk's mask is its own voxels only, so edits and loaded changes update it with the re-mesh they already cause. Other cameras (a mirror, say) see the world as the main camera does, with the hidden chunks missing: turn it off for those. `Volume.CulledChunkCount` and `PicaVoxelStats.CullWalks` show what it is doing.

**Cull World Floor** is the simple part: below the bottom row of chunks there is nothing, so the downward faces of its lowest voxels (which face the void) aren't made, in the render mesh or the collider.

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

## Landscape generator

**LandscapeGenerator** makes terrain with more going on than **TerrainGenerator**'s single layer of noise, along the lines of Minecraft's (much simplified): lowlands with lakes and sandy shores, rolling hills, mountain ranges with ridged peaks, bare rock on cliffs and snow on the summits, tunnels winding through the ground (now and then breaking out at the surface) and the odd cavern, a bedrock floor, and trees spread through forested areas (add a **TreeProcessor**, scheduled After Generation, for their leaves). Everything is under its Inspector headers: heights, how mountainous, rock and snow lines, tunnel width and caverns, forest cover. It costs about the same per chunk as TerrainGenerator: heights are worked out every 4 columns and caves every 4 blocks, blended between.

Its sand, snow and water blocks (values 14, 15 and 16) are on **Textures/minecrafttiles-pv-landscape**, the original tile sheet with those three added (the original sheet is unchanged), with a tile array and the **Materials/URP/PicaVoxel URP Landscape Terrain** material made from it. Custom block meshes keep their UVs, made for the original 14-column sheet: the material's **Tile Sheet Columns Their UVs Were Made For** (14, padding 1) maps them onto the wider sheet's array. A block preview that tiles its texture by the manipulator's Max Value wants the new sheet and a Max Value of 16. For water, add a custom block to the Volume with its value, **Has Transparency** and **Hide Faces Against Same** (only a lake's outside is drawn and collides, not the faces between its blocks). Water stays solid to the touch, so the manipulator can still select and build against it. (**No Collision** leaves a block out of the collision mesh altogether, but then nothing can be edited against it either.) The tile array terrain shader draws the block set as its **Water Block Value** see-through, as a fine dither (so the terrain stays one opaque material), and casting no shadow.

Switching an existing world to a new generator changes the land under any edits players have saved there, since saved edits are kept as changes to the generated terrain.

# AgentHub crab mascot

The crab at the top right of the main window (`src/AgentHub/Controls/CrabMascot.xaml.cs`). Its sprites were made with
PixelLab from `reference.png`, the crab of the AgentHub logo artwork.

## Character description (PixelLab prompt)

> Cute chubby cartoon crab mascot, front view facing the viewer. Glossy bright orange shell with soft yellow-orange
> highlights on top and darker red-orange shading underneath. Wide smooth oval dome body, wider than tall. Row of five
> small round bumps along the top edge of the shell, the middle bump biggest. Two large shiny dark-brown oval eyes with
> a white sparkle highlight, set directly on the front of the shell (no eye stalks). Small happy curved smile. Both big
> claws raised high on either side of the body, held up like cheering; each claw is a large rounded pincer with an open
> notch, shiny orange. Three short curved pointed legs on each side underneath the body. Rounded friendly shapes,
> adorable app mascot, clean dark outline.

Generated with `create_image_pro`, 64x64, transparent background, `reference.png` passed as reference image with the
usage note "character base: copy this exact crab design, shape, colours, eyes, claws and legs". One call returned 16
candidates (`candidates.png`, a 4x4 grid of 64x64 cells): candidate 1 is the mascot; the other 15 are spare expressions (wink, laugh,
heart eyes, surprised, angry, sad, sleepy, dizzy, pirate, party hat, sunglasses, sailor hat, crown) for later use.

## Animations

Candidate 1 was centred on an 80x80 canvas, feet at row 76, to leave headroom for hops and rolls. Each animation was
made with `animate_image_pixminimax` (subject "a cute chubby glossy orange cartoon crab with big dark eyes, a smile,
two big raised claws and six short legs", pose "standing front-on facing the viewer, both claws raised high", direction
south, view side). Frame 0 of every strip is the resting pose.

| Strip (`src/AgentHub/Assets/Crab/`) | Frames | Motion prompt |
|---|---|---|
| `idle.png` | 9 | gentle idle breathing loop: body bobs slightly up and down, claws sway a little, blinks once, stays in place |
| `scuttle.png` | 9 | scuttling sideways like a crab, six legs stepping quickly in alternation, body bobbing and leaning slightly, still facing the viewer, stays centred in place (walk cycle loop) |
| `pinch.png` | 9 | snapping both claws open and shut twice, pinching the air, cheeky grin, body stays in place |
| `wave.png` | 9 | waving hello: the right claw swings side to side in a friendly wave while the left claw stays raised, body stays in place |
| `roll.png` | 13 | tucks its claws and legs in, curls up into a round ball, rolls over in one full somersault, then pops back open to standing with claws raised |
| `hop.png` | 9 | startled by a poke: eyes go wide, it hops straight up with claws flung up and legs splayed, lands, squashes a little and settles back to its pose |
| `dance.png` | 9 | happy little dance: sways body left and right, claws clapping together above its head, legs tapping, ends back in the starting pose (loop) |
| `sleep.png` | 9 | getting sleepy: yawns, eyes slowly close, claws lower down to rest on the ground, body sinks low and dozes off (played backwards to wake) |

Crabs walk sideways, so the front view serves every direction: the app moves the sprite while `scuttle` plays, and
moves it during frames 4 to 10 of `roll` (the curled-up tumble) to roll along the header.

## Faces (`faces.png`, added 2026-11-09)

The 16 candidates (PixelLab gallery image `04145b34-489e-56f1-92e6-3f14cd54c174`, 64x64 each; `candidates.png`
here) laid out as one 80x80-cell strip, each cell placed at (8, 25) so the body lines up with the resting pose of every
animation (the resting crab spans columns 12 to 67 and rows 32 to 75 in both). No pixel was changed or scaled. Cell
order: smile, wink, laugh, heart eyes, shocked, angry, sad, sleepy, tongue out, dizzy, pirate hat, party hat,
sunglasses, shy, sailor salute, crown (`CrabMascot.Face`). The app holds one still for a second or so: a cheerful one at
random between outings, dizzy after every roll, the party hat (then a dance) when a job it was busy with ends. Shocked,
angry, sad and sleepy are not used yet.

Cost: 10 generations for the candidates, 1 for a faithful conversion that was not used, 8 for the animations.

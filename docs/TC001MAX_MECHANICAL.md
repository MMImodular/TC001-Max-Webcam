# TOPDON TC001 Max: mechanical reference

Reference geometry for the planned 1/4"-20 tripod adapter, which reroutes the
camera's USB connector through a custom flex PCB. The Fusion 360 design
"TOPDON TC001 Max" is built from the numbers below. Every user parameter in it
has the same name as the **Param** column here, so correct both together once
the camera has been measured with calipers.

Status key:

- **Confirmed**: stated by TOPDON, or fixed by the USB Type-C standard.
- **Listing**: stated by retailers or reviews. Plausible, but not from TOPDON's own spec table.
- **Estimate**: scaled from a product render by me. Expect ±1 mm or worse.

Nothing here has been measured on a real unit yet.

## The Fusion design

- **Where:** Fusion 360, project **Default Project**, folder **TC001 Max**, design **"TOPDON TC001 Max"**.
- **Kind:** one parametric Part document (Y-up, mm).
- **Bodies:** `Shell`, `EndCap_MinusX`, `EndCap_PlusX`, `USB_Collar`, `USB_C_Plug`, and `PlugEnvelope` (translucent keep-out).
- **Hidden construction planes:** `FRONT_lensFace`, `MOUNT_back`, `MOUNT_bottom`, `MOUNT_top`, `PLUG_seat`, `PLUG_tip` and `END_plusX`.
- **Construction axis:** `PLUG_axis`.

Every sketch is fully constrained. Each vertex is dimensioned from a fixed
datum by an expression in the user parameters. To correct the model, edit
values in *Modify › Change Parameters*. Each parameter's comment says whether
its value is confirmed or estimated.

The parameter `sk_ref` (100 mm) is only that sketch datum. Don't edit it.

## Coordinate frame used in the model

The body is drawn front view on: **lens face toward +Z, USB plug pointing +Y,
lens end toward −X**. In other words, you are looking at the lens face with
the plug at the top and the lenses on the left.

- X = 0 at the middle of the body length.
- Y = 0 on the top face (plug side), so the body spans Y = −`body_H` … 0.
- Z = 0 on the front (lens) face, so the body spans Z = −`body_T` … 0.

In use the camera hangs *below* the phone, upside down compared with this
view. The lens face points the same way as the phone's rear camera.

## Overall body

| Item | Value | Param | Status | Basis |
|---|---|---|---|---|
| Weight | 33 g | – | Confirmed | TOPDON sell sheet [1]; same in [2][4] |
| Length (X) | 71.0 mm | `body_L` | Confirmed / Listing | "7.1 × 4.2 cm" [2][3]; 2.8 in [4] |
| Thickness (Z) | 14.0 mm | `body_T` | Listing | 0.55 in [4]; the original TC001 is also listed as 14 mm [3] |
| Height of body face, plug edge to opposite edge (Y), plug excluded | 31.2 mm | `body_H` | **Estimate** | Front render [5] is 443 × 195 px, scaled to 71 mm (6.24 px/mm). See the note below. |
| "Width" as listed | 42 mm (1.65 in) | – | Listing, **conflicts** | [2][3][4]. The render puts body plus plug at ≈ 40 mm, so 42 mm probably includes the plug. |
| Housing | Blue metal-look main shell; black end caps on both X ends | – | Observed in renders [5][6][7] | Material not stated anywhere. It looks like anodised aluminium with plastic caps. |
| End cap thickness (X) | 2.0 mm | `endcap_W` | **Estimate** | Renders [6][7] |
| Corner chamfers, plug-side corners (front view) | 6.0 mm | `chamfer_top` | **Estimate** | Render [5]: 42 px across × 34 px down |
| Corner chamfers, bottom corners (front view) | 2.5 mm | `chamfer_bot` | **Estimate** | Render [5] |
| Bevel on the front-top long edge, height on the front face | 5.6 mm | `bevel_front` | **Estimate** | Lighter band at the top of the straight-on render [5] (35 px) |
| Same bevel, depth into the top face | 3.0 mm | `bevel_top` | **Estimate, weak** | No side view found. I picked a value that keeps the flat top wide enough for the plug collar. |
| Bottom long edges | ≈ 1 mm round | `edge_R` | **Estimate** | Renders |

**Check on the render scale.** The scale assumes the 71 mm length is right.
Scaled that way, the USB-C plug in the same render measures 8.46 mm wide.
The standard plug shell is 8.25 mm wide, so the two agree within 2.5 %.
The lens rings in the render are circular (61 × 63 px and 69 × 67.5 px), so
the image is not stretched. That makes the 31 mm body height credible, and
leaves the listed "42 mm" as body plus plug.

**Mounting surfaces** (flat in every render):

- **Back face** (Z = −`body_T`). About 71 × 31 mm, featureless. This is the best clamping or bonding face.
- **Bottom face** (Y = −`body_H`, the face opposite the plug). About 66 × 13 mm between the corner rounds.
- **Top flat** either side of the plug collar. About 9 mm deep (Z) behind the front bevel.
- **End faces** (black caps). The +X cap has a pinhole, so avoid it.

These are exposed in the Fusion design as construction planes `MOUNT_back`,
`MOUNT_bottom`, `MOUNT_top` and `PLUG_seat`.

## USB connector (most important for the adapter)

| Item | Value | Param | Status | Basis |
|---|---|---|---|---|
| Type | **Fixed male USB-C plug**, rigid, not rotating or folding | – | Confirmed by every render [5][6][7] | The plug goes straight into the phone's port. "Connector is constructed with extended clearance … plug directly into most phones without removing protective cases" [2]. |
| Location | Centre of the long top edge (the +Y face), pointing +Y, perpendicular to the lens face's long axis | – | Observed | Render [5] |
| Offset of plug centre along X from body centre | 0.0 mm (measured +0.5 mm, within error) | `plug_X` | **Estimate** | Render [5]: plug centre at 302.5 px, body centre at 299.5 px |
| Distance of plug centre behind the front (lens) face | `body_T`/2 = 7.0 mm (centred) | `plug_Z` | **Estimate, weak** | No side view found |
| Plug shell width × thickness | 8.25 × 2.40 mm, end radius ≈ 1.2 mm | `usbc_W`, `usbc_T` | Confirmed (USB Type-C spec plug outline) | USB Type-C Cable & Connector Spec, plug interface dimensions |
| Exposed plug length, collar face to tip | 7.7 mm | `usbc_L` | **Estimate** | Render [5]: 48 px. Spec minimum mating length is 6.65 mm, so ≈ 1 mm extra is plausible for case clearance. |
| Black collar (base) under the plug, X × Z × height | 10.4 × 4.0 × 1.0 mm | `collar_W`, `collar_D`, `collar_H` | **Estimate**; the Z depth is a guess | Render [5]: 65 px wide, 6 px tall |
| Total protrusion above the top face | 8.7 mm | `collar_H + usbc_L` | **Estimate** | – |
| Electrical | USB 2.0 device, VID 0x3474, PID 0x4962 (UVC). Bus-powered, < 0.38 W | – | Confirmed (this repo's protocol work; power from [3]) | – |
| Orientation | USB-C is reversible; the app flips the image as needed | – | – | – |

**What's in the box** [1]: the TC001 Max, a USB-A to USB-C cable, a USB-C to
USB-C adapter, a USB-C to Lightning adapter, a microfibre cloth, a manual and a
carrying bag. TOPDON doesn't say what each adapter does. The male plug on the
camera implies the cable has a female C end, so it works as a PC lead or
extension.

**Plug envelope in the model.** `PlugEnvelope` is a box around the collar and
plug, `env_clr` = 0.5 mm larger on every side and over the plug tip. The
adapter's receptacle and flex must not cut into it. A mating USB-C receptacle
seats when its face reaches the collar face at Y = `collar_H`. That face is
the datum `PLUG_seat`.

## Lenses (front face)

| Item | Value | Param | Status | Basis |
|---|---|---|---|---|
| Layout | Two lenses stacked across the height, inside one slanted black window at the −X end of the front face | – | Observed [5][6][7] | – |
| Window: top edge below the top face | 6.0 mm | `win_top` | **Estimate** | Render [5] |
| Window height | 23.6 mm | `win_H` | **Estimate** | Render [5] |
| Window width (along X) | 20.0 mm | `win_W` | **Estimate** | Render [5] |
| Window: bottom-left corner from the −X end | 5.3 mm | `win_X0` | **Estimate** | Render [5] |
| Window slant (top edge shifted toward +X) | 9.85 mm, ≈ 22.6° from vertical | `win_shear` | **Estimate** | Render [5] |
| Upper lens (nearer the plug), likely the **visible camera**. Centre from the −X end, centre below the top face, ring diameter | 19.9, 12.3, Ø 9.9 mm | `vis_X`, `vis_Y`, `vis_D` | **Estimate**; which lens is which is also a guess | Render [5]. The small dark pupil looks like a CMOS camera module. |
| Lower lens, likely the **thermal lens**. Centre from the −X end, centre below the top face, ring diameter | 19.7, 24.1, Ø 10.6 mm | `ir_X`, `ir_Y`, `ir_D` | **Estimate** | Render [5] |
| Thermal lens focal length | 3.2 mm, fixed focus | – | Confirmed [1] | – |
| Visible camera | 1.5 MP | – | Confirmed [1] | – |
| Thermal sensor | 256 × 192, 25 Hz, ≤ 40 mK | – | Confirmed [1][3] | – |
| Field of view | Not published for the Max. The original TC001 is 56° × 42° [3]. | – | Unknown | Keep the adapter well behind the front face plane. |

**Other features**

- **Pinhole on the +X end cap.** About Ø 0.8 mm, at mid-height, toward the
  front (`pin_D`, `pin_Y`, `pin_Z`, all estimates). Visible in [6][7] and on
  the original TC001. Its purpose (LED, mic or
  vent) is unknown, so don't cover it.
- **No** buttons, tripod thread, lanyard hole or clip appear in any image.
- **No** battery. The camera is powered from the host.

## Measure first with calipers (in this order)

1. **Body height `body_H`** (plug edge to opposite edge, plug excluded), and
   **total height including the plug**. This resolves the 31 mm vs 42 mm
   conflict, and every other estimate is scaled from it.
2. **USB plug datums:** `usbc_L` (collar face to tip), collar `collar_W` ×
   `collar_D` × `collar_H`, `plug_X` (plug centre to each end), and `plug_Z`
   (plug centre to the front face and to the back face).
3. **`body_T`** and whether the back face is truly flat. It is the main
   bonding or clamping face.
4. **Front-top bevel** (`bevel_front`, `bevel_top`) and the corner chamfers.
   These decide how a clamp or shroud wraps the top edge next to the plug.
5. **Lens and window positions.** Also check which lens is thermal: cover
   one and watch which stream goes dark.
6. End cap thickness and pinhole position.

## Sources

1. TOPDON, *TC001 Max Sell Sheet* (PDF), <https://web-file.topdon.com/topdon-web/information_download/TC001-Max-Sell-Sheet.pdf>. Weight 33 g, lens 3.2 mm, box contents, sensor specs.
2. GarageTechAdvisor, *TOPDON TC001 Max Review*, <https://garagetechadvisor.com/product-review/topdon-tc001-max-review-b0gbwrd2jt/>. "7.1 × 4.2 cm; 33 g"; connector "extended clearance" quote.
3. EasyMeasureLab, *TOPDON TC001 vs TC001 Max*, <https://easymeasurelab.com/topdon-tc001-vs-tc001-max/>. TC001 71 × 42 × 14 mm, 30 g, 56° × 42°; Max "about 2.8in long", 33 g, < 0.38 W.
4. BatteryMart listing, <https://www.batterymart.com/p-tc001max-topdon-dual-lens-thermal-camera.html>. 2.8 × 1.65 × 0.55 in. Seen only through search-result text; the page refused direct fetches.
5. vepro.ca product image (straight-on front render), <https://vepro.ca/wp-content/uploads/2026/03/vp-td521d0092.jpg>. Primary source for all photo-scaled values (6.24 px/mm).
6. Amazon listing image, <https://m.media-amazon.com/images/I/41GqURILLeL._SL500_.jpg>. Three-quarter render.
7. GarageTechAdvisor hero render, <https://garagetechadvisor.com/wp-content/uploads/2026/11/topdon-tc001-max-thermal-camera-hero.webp>.
8. USB-IF, *USB Type-C Cable and Connector Specification*. Plug shell outline 8.25 × 2.40 mm; mating length.

Sources I could not use:

- TOPDON's own product pages (topdon.com, topdon.us and au.topdon.com) returned HTTP 429 to every automated fetch.
- The manuals.plus copies of the user manual returned HTTP 403.
- I found no teardown and no FCC filing for the Max. TOPDON's FCC grantee code is 2AVYW, but no TC001 Max ID turned up.

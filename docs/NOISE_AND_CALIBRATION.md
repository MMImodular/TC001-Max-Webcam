# Measuring per-pixel drift on the TC001 Max: calibration techniques

Written by Claude (2026-11-21) at the owner's request, to plan the noise study in
`experiments/noise/`. It reviews the published ways of calibrating uncooled microbolometer cameras
and recommends how to measure **per-pixel drift over time** on this camera. Every source is listed
under References at the end; no text or code was copied from them.

## Summary

- **Each pixel's drift has to be measured against that pixel's own past.** A static scene
  doesn't have to be uniform, but it does have to stay the same. The scene's temperatures cancel
  when you subtract a pixel's value at the start from its value later, so whatever remains is drift.
  The only exception is when the scene itself changes temperature, for example a room cooling
  overnight. Subtracting neighbouring pixels from each other measures the scene, not the camera.
- **A uniform reference in front of the lens makes the measurement clean.** It could be a blackbody,
  or a matte plate that fills the field of view. Every pixel then sees the same radiance, so any
  pixel-to-pixel difference is non-uniformity. The difference between the start and later is
  drift. This is the standard method, and the cheapest one: see "Recommended protocol".
- **The camera's own shutter is a uniform reference too.** With the automatic shutter off, a
  manual shutter at the end of the night resets the offsets. The jump in the picture at that
  moment equals the per-pixel offset error that built up since the last shutter. The overnight
  script records that jump as an independent check.
- **A single reference temperature gives offset only.** Measuring gain (responsivity) and its
  drift needs **two reference temperatures** (two-point non-uniformity correction). Drift that
  depends on temperature needs the camera's internal temperature, which is probably one of the
  metadata words.

## What drifts in a camera like this

The TC001 Max uses a 256 x 192 uncooled VOx microbolometer array, probably in an InfiRay
AC020-family module (see `docs/PROTOCOL.md`). It has no thermo-electric cooler. Each pixel's
output can be written as

    x_p(t) = g_p(T_fpa, t) · L_scene,p + o_p(T_fpa, t) + n_p(t)

- **g_p, the gain (responsivity):** differs from pixel to pixel by a few percent. It is
  calibrated at the factory into a gain table.
- **o_p, the offset:** the large term. It changes with the temperature of the detector array
  (T_fpa) and of the housing and lens, because the pixels also see the inside of the camera.
  Without a cooler, it drifts whenever the camera warms or cools. **This is what the shutter
  exists to correct:** the camera closes a uniform flap and stores each pixel's reading as its
  new offset (flat-field correction, FFC). Between shutter events the offsets drift away again.
  That residual drift is what we want to see.
- **n_p, the temporal noise:** white (thermal and Johnson) noise plus 1/f noise. The 1/f part
  makes very long averages stop improving. Readout circuits also add **row and column noise**
  (stripes) that flicker from frame to frame.

The camera does not give us raw detector counts. It gives temperatures after its own correction:
factory gain and offset tables, the shutter offsets, its radiometric conversion, and possibly
temporal filtering. So we measure **the residual error of the camera's own corrections**, in
temperature units (1 count = 1/64 K = 15.6 mK). Quantisation alone adds about 4.5 mK RMS.

## Techniques

### 1. Shutter (one-point offset) correction: built in

The camera does this automatically about every 60 s (measured in `experiments/LOG.md`). With
`SetAutoFFCStatus(0)` it stops, and the per-pixel offsets drift freely. Uses for drift
measurement:

- **The jump at a manual shutter.** Take the mean of the 10 s after the shutter minus the 10 s
  before it, pixel by pixel. Remove its median and its smooth part, and what remains is the
  per-pixel offset error that built up. The scene can be anything, as long as it doesn't change
  during those few seconds.
- **Repeated manual shutters** (for example every 30 min) give drift at 30-minute steps. However,
  each one resets the drift, so you lose the long free-running curve. Tonight's run does one
  shutter at the start and one at the end.
- Limitation: we never see the shutter image itself. While the shutter is closed, the camera
  repeats the last frame. A shutter at the camera's own temperature also gives offset only,
  not gain.

Nugent, Shaw and Pust (2014) go further. They treat the internal shutter as an equivalent
blackbody, with a characterised lens and shutter, and use it for radiometric calibration of
cameras without a cooler.

### 2. External uniform reference (flat field): best for per-pixel drift

Put a uniform, high-emissivity surface over the whole field of view, close to the lens, and leave
it there. This gives **direct per-pixel drift with no scene to separate out**.

- **Lab grade:** an extended-area blackbody (a flat plate with controlled temperature).
- **DIY version:** a thick aluminium plate, a few mm, larger than the lens's view. Paint it matte
  black with high-emissivity paint, or cover it with black electrical tape (emissivity about
  0.95). Tape a thermistor or thermocouple to the back. Mount it a few mm to a couple of cm in
  front of the lens; it will be out of focus, which helps hide its texture. Shield it from air
  currents, for example inside a cardboard box. The plate's mass smooths out room-temperature
  changes, and the thermistor tells you how much of the global change is the plate.
- **Analysis:** subtract each pixel's start value, then subtract the frame median, which is the
  plate plus any global offset. What remains is per-pixel drift with no scene in it.
  Separately, the frame median minus the thermistor reading is the camera's global (absolute)
  drift.
- **What can go wrong:**
  - the plate's own non-uniformity and gradients;
  - reflection of the camera's own detector back at itself (the narcissus effect); tilt the
    plate a few degrees to avoid it;
  - fingerprints and dust.

### 3. Two-point correction (gain and offset)

Show the camera two uniform references at two temperatures, T1 and T2. For each pixel, solve
`x = g·L(T) + o` for g and o (Scribner, Kruer and Killiany 1991). Repeat later and compare to
get **gain drift and offset drift separately**.

- **DIY version:** the room-temperature plate from technique 2 plus a second plate warmed 10 to
  20 °C above it. The warm plate could be heated with a resistor and controlled by its
  thermistor, or it could be a black-coated container of warm water. Swap them by hand or with a
  servo.
- **Multi-point versions** (three or more temperatures) also fit the non-linearity.

### 4. Correction that depends on the camera's temperature (shutterless methods)

Gain and offset depend mostly on the temperatures of the detector array and the housing. Measure
the correction at several camera temperatures, for example in a box warmed slowly. Then model it
as a function of the internal temperature and apply it without a shutter. Nugent, Shaw and Pust
(2013) did this for cameras without temperature stabilisation; Tempelhahn and colleagues (2016)
did it for a fully shutterless camera with extra temperature sensors inside.

For the TC001 Max:
- The first step is to find which **metadata word tracks the internal temperature**.
  `analyze_noise.py` lists the words that change during a run and ranks them by how well they
  follow the global offset.
- After that, you can model per-pixel drift as a function of that temperature instead of time.

### 5. Scene-based non-uniformity correction

These methods estimate the correction from the video itself:
- **Constant statistics** (Harris and Chiang 1999) assumes every pixel sees the same mean and
  spread over time.
- **Registration** (Hardie and colleagues 2000) tracks the same scene point as it crosses
  different pixels when the camera moves.

These methods need a **moving scene or a moving camera**. They fail on a static scene, where they
"learn" the scene as fixed-pattern noise and leave ghosts. So they can't measure drift tonight,
but they are a possible later feature for the webcam: they could reduce shutter clicks while the
camera pans.

### 6. Static but non-uniform scene: what tonight's run does

There is no reference target tonight, so the run relies on the scene staying the same and
compares each pixel with itself. For 10-s mean frames M_k, the change since the start is split
into three parts:

    D_k(p) = M_k(p) - M_0(p) = g_k + s_k(p) + r_k(p)

- **g_k, the global offset:** the frame median. It contains both the camera's global drift and
  the room's overall temperature change, and the two can't be told apart without a reference
  thermometer in the scene.
- **s_k, a smooth field:** a heavily blurred D_k. This is where the scene changing unevenly
  goes, for example a window cooling faster than a wall. Large-scale camera drift, such as
  vignetting changing as the lens cools, also ends up here, so s_k is an upper bound, not a
  clean measurement.
- **r_k, the per-pixel remainder:** the drift we want.

Checks built into the analysis:
- **Temporal noise floor.** The noise of a 10-s mean is about the per-pixel noise divided by
  √250. r_k only counts as drift when it is well above that.
- **Correlation with scene detail.** If r_k follows the scene's own fine structure (edges and
  objects), it is either a gain change or a real change in the scene, not offset drift.
- **The end shutter.** The per-pixel jump at the final manual shutter should match −r_final.
  That is the strongest evidence the measurement is right.

Limits of this method:
- Edges of objects whose temperature changes overnight leak into r_k. A static but warm or
  cooling object, such as a PC going idle, is the main hazard.
- Air currents and people moving through the scene also leak in. Seconds with a person in
  frame are flagged and left out.

### 7. Noise statistics used

- **Temporal noise per pixel (an NETD proxy):** the standard deviation of each pixel over a
  60-s burst at 25 fps, after removing each pixel's linear trend. A true NETD (noise-equivalent
  temperature difference) needs a uniform blackbody at a known temperature. On a scene of
  mixed objects, this proxy is close but not exact.
- **The "3D noise" split** (D'Agostino and Webb 1991; the US Army night-vision lab's method):
  - σt, the whole frame flickering;
  - σtv, rows flickering;
  - σth, columns flickering;
  - σtvh, random per-pixel noise.
  The fixed-pattern terms (σvh and so on) are the drift maps above.
- **Power spectral density (Welch's method)** of the frame mean and of pixel minus frame mean.
  White noise is flat; 1/f noise and drift rise towards low frequencies.
- **Allan deviation:** how much averaging over a time τ helps. It falls as 1/√τ while white
  noise dominates, reaches a minimum, then rises where drift wins. The τ at the minimum is the
  longest useful averaging time, and a sensible interval between shutter events if you want
  the lowest noise.

## Recommended protocol for this camera

1. **Warm up for 30 to 60 min** with the camera streaming before measuring. Most offset drift
   happens while the camera heats itself after power-on.
2. **Flat-field drift run (best):** use the DIY plate with a thermistor, with the auto shutter
   off. Do one manual shutter at the start and one at the end, and record overnight. Per-pixel
   drift is then frame-minus-start-minus-median, with no scene to separate out. The overnight
   script works unchanged: point the camera at the plate instead of the room.
3. **Repeat with two plate temperatures** (two-point correction) to separate gain drift from
   offset drift.
4. **Temperature dependence:** warm the camera slowly in a box over about 10 °C, read the
   metadata word that follows its temperature, and fit offset and gain per pixel against it.
5. **Shutter interval:** use the Allan-deviation minimum and the drift rate (mK per hour) to
   decide how often the webcam needs a shutter event. The webcam already has the
   `auto_shutter` and `idle_auto_shutter` settings.

## Things that can spoil the measurement

- **The camera's own processing.** The ISP may filter over time, which hides noise. Check the
  autocorrelation of a pixel from frame to frame: white noise shows none.
- **Picture-leak frames** (about 240 °C, see `docs/PROTOCOL.md`), **stalls** and **repeated
  frames** are counted and skipped by the capture.
- **Warm-up, air currents** (HVAC turning on and off), **sunlight** (hence ending at sunrise),
  and **people and pets**. A person seen during the recording is flagged, not stopped.
- **Absolute temperature.** The camera read about 26 to 28 °C in a room near 20 °C. Drift is
  relative and unaffected, but don't use the camera's own reading as the reference temperature.

## References

- D. A. Scribner, M. R. Kruer, J. M. Killiany, "Infrared focal plane array technology",
  *Proc. IEEE* 79(1), 1991. The two-point correction and the origins of non-uniformity.
- J. A. D'Agostino, C. M. Webb, "Three-dimensional analysis framework and measurement methodology
  for imaging system noise", *Proc. SPIE* 1488, 1991. The 3D noise model.
- J. G. Harris, Y.-M. Chiang, "Nonuniformity correction of infrared image sequences using the
  constant-statistics constraint", *IEEE Trans. Image Processing* 8(8), 1148-1151, 1999,
  doi:10.1109/83.777098.
- R. C. Hardie, M. M. Hayat, E. Armstrong, B. Yasuda, "Scene-based nonuniformity correction with
  video sequences and registration", *Applied Optics* 39(8), 1241-1250, 2000,
  doi:10.1364/AO.39.001241.
- P. W. Nugent, J. A. Shaw, N. J. Pust, "Correcting for focal-plane-array temperature dependence
  in microbolometer infrared cameras lacking thermal stabilization", *Optical Engineering* 52(6),
  061304, 2013, doi:10.1117/1.OE.52.6.061304.
- P. W. Nugent, J. A. Shaw, N. J. Pust, "Radiometric calibration of infrared imagers using an
  internal shutter as an equivalent external blackbody", *Optical Engineering* 53(12), 123106,
  2014, doi:10.1117/1.OE.53.12.123106.
- A. Tempelhahn, H. Budzier, V. Krause, G. Gerlach, "Shutter-less calibration of uncooled infrared
  cameras", *J. Sens. Sens. Syst.* 5, 9-16, 2016, doi:10.5194/jsss-5-9-2016.
- D. W. Allan, "Statistics of atomic frequency standards", *Proc. IEEE* 54(2), 1966. The Allan
  deviation.
- P. D. Welch, "The use of fast Fourier transform for the estimation of power spectra", *IEEE
  Trans. Audio Electroacoustics* 15(2), 1967. The averaged periodogram.
- H. Budzier, G. Gerlach, *Thermal Infrared Sensors: Theory, Optimisation and Practice*, Wiley,
  2011. Background on microbolometer noise, drift and calibration.

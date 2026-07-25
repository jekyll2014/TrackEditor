# Race Analysis — development plan

Branch: `RaceAnalysis`

## Goal

Learn an athlete's **speed-vs-terrain** behaviour from one or more recorded tracks, store it as a
portable **race model**, and apply it to any planned track to predict pace — injecting timestamps into
a copy so the user sees the expected race flow (splits, ETA per waypoint, finish time).

## Domain grounding

This is **Grade-Adjusted Pace (GAP) + fatigue**, a known problem. We fit the athlete to a
Minetti-style asymmetric cost-of-running curve rather than inventing one:

- Speed vs grade is **asymmetric** and non-monotone: steep downhill *slows* a runner (braking /
  technical), so keep signed grade and both halves of the curve.
- Fatigue is real over long efforts and is best driven by **cumulative effort** (ascent / energy /
  elapsed), not raw distance. With HR present, fatigue is **observed** (aerobic decoupling: HR drifts
  up for the same pace), not guessed.

## Reference data (validation pair)

| | Fit = `MonteRosa_50K_Olga.gpx` | Predict = `WWT_50k_by_UTMB.gpx` |
|---|---|---|
| what | Olga's actual race run | official planned route, same course |
| points | 29,948 @ 1 Hz | 2,669, variable spacing |
| time | 07:15 -> 15:35 (8h20) | none |
| type | running (race intent) | — |
| HR / cad | every point (`ns3:` namespace) | none |
| temp | none | none |

Second sample `Morning_Hike.gpx` (Strava) carries HR/cad **and** `atemp` under the `gpxtpx:` namespace
— use it to prove multi-namespace + temp parsing.

### Proven data gotchas

1. **Extension namespace varies**: Strava `gpxtpx:hr`, Garmin `ns3:hr`. Match by **local-name**,
   ignore prefix. Extensions are nested (`trkpt > extensions > TrackPointExtension > hr`).
2. **HR needs cleaning defensively**: the Monte Rosa file is actually clean (true range 86–162 bpm), but
   real recordings carry dropouts (lost strap → implausible bpm) and single-sample spikes, so clamp to a
   plausible window + median de-spike before use. (Note: grepping raw HR naively catches the `3` in the
   `ns3:` namespace prefix — extract the element *value*, not any digit on the line.)
3. **Garmin temp unreliable** -> temp OFF by default; only offered when present.
4. **1 Hz, ~30 k points/track** -> resample/decimate before fitting (perf + noise).
5. **Grade = derivative of elevation** -> amplifies noise -> must smooth elevation before differentiating.

## Decisions locked

- **HR in v1**: yes. Present a **"detected signals — confirm which to use"** checkbox panel per analyze
  (ele always; HR / cadence / temp toggleable). Temp default off. Missing-signal tracks fall back
  gracefully (fatigue -> cumulative ascent).
- **Model fields**: add nullable `Hr` / `Cad` / `Temp` to `TrackPoint`.
- **Multi-track blend**: **normalize each track to its own intensity baseline** before pooling.

## Model (separable, interpretable)

```
predictedSpeed(grade, effort, ele, surface, temp)
  = baseCurve(grade)          # m/s vs signed grade, fresh & intensity-normalized
  x fatigueMult(effort, hr)   # 1.0 -> decays; calibrated from HR drift when available
  x altitudeMult(ele)         # physiology derate above ~1500 m
  x surfaceMult(tag)          # user range-tag, default mid-quality trail = 1.0
  x tempMult(temp)            # mild; off unless user opts in
```

Separable => each track fills the whole grade curve, factors fit/toggle independently, model is small
and human-readable.

### Race model schema (`*.racemodel.json`)

```jsonc
{
  "version": 1,
  "meta": { "sourceTracks": ["..."], "totalKm": 50.2, "fitDateUtc": "...", "signalsUsed": ["hr","cad"] },
  "baseCurve": { "gradeDeg": [-25, 25], "speedMps": ["..."] },   // 1-deg bins, smoothed, gaps interpolated
  "fatigue":   { "driver": "cumAscent|elapsed|energy", "model": "linear|exp", "k": 0.00012,
                 "hrDrift": { "used": true, "coeff": 0.0 } },
  "altitude":  { "refM": 1500, "deratePerKm": 0.025 },
  "athleteBaseline": { "flatSpeedMps": 2.6, "refHr": 148 }       // intensity normalization anchor
}
```

## Analyze pipeline (fit)

1. **Parse** selected tracks incl. extensions (local-name match).
2. **Clean** signals: despike/clamp HR, drop nulls.
3. **Resample** to fixed spacing (~5–10 m); **smooth** elevation (Savitzky-Golay / moving avg ~30–50 m);
   smooth heading separately for turn metrics.
4. **Segment features**: signed grade (deg), speed (m/s), cum distance, cum ascent, elapsed, HR, cad,
   heading-change density.
5. **Rest detection**: exclude segments with speed < threshold & cad ~ 0 (aid stations); tally aid time
   separately. Reuse existing `MovingSpeedMps`.
6. **Intensity-normalize** each track to its own baseline (flat-ground speed / speed-at-HR), then pool.
7. **Fit `baseCurve`**: 1-deg grade bins, robust central speed per bin, min-sample gate, interpolate empty
   bins, smooth.
8. **Fit `fatigueMult`**: regress residual (actual/base) vs cumulative effort; if HR on, also vs HR
   drift. Emit decay coefficient(s).
9. **v2 factors** (optional): turn penalty (residual vs heading-change density), roughness (residual vs
   grade stdev), altitude (residual vs ele).
10. **Emit** `RaceModel` JSON; show fit summary + curve preview.

## Apply pipeline (predict)

1. Load target track; **resample/smooth identically**. Fill missing elevation from SRTM / online
   (existing `SrtmService` / `OnlineElevationService`).
2. Sequentially integrate from **start time (default 08:00)**: per segment grade -> `baseCurve`
   x fatigue(accumulated effort so far) x altitude x surface(range-tag) x temp(if on) -> segment speed
   -> segment time; feed accumulated effort forward.
3. Add user-tagged aid-station stop time if any.
4. **Inject `Time`** into each point of a **copy** track named `"<name> (predicted)"`.
5. Summary: finish time, per-waypoint ETA, moving vs total.

## UI

- **"Analyze Race Ability…"** — enabled with >=1 track selected. Dialog: list selected tracks + per-track
  detected-signal checkboxes; global options (fatigue driver, normalization on/off); Run -> fit summary +
  grade-curve preview + **Export Model** (`*.racemodel.json`).
- **"Apply Race Model…"** — enabled with exactly 1 track selected. Dialog: **Import Model**; start time
  (default 08:00); target-track defaults (surface tag by point-range, temp, aid stops); Run -> creates the
  predicted copy + shows splits/finish.
- Model **export/import** = plain file dialogs.

## Code placement

- **Core model**: `TrackPoint` gains nullable `Hr` / `Cad` / `Temp`; `Clone` copies them;
  **exclude from `ContentHash`** (sensor data isn't a user "edit"). `GpxIo.ParsePoint` reads nested
  extensions by local-name; `GpxIo.Write` optionally re-emits extensions (round-trip).
- **New** `TrackEditor.Core/Services/RaceAnalysis/`:
  `RaceModel.cs` (schema + JSON IO), `SignalCleaning.cs`, `TrackResampler.cs`, `RaceAnalyzer.cs` (fit),
  `RacePredictor.cs` (apply).
- **UI** `TrackEditor/`: `AnalyzeRaceWindow.xaml(.cs)`, `ApplyRaceModelWindow.xaml(.cs)`; wire two buttons
  in `MainWindow`.
- **Tests/validation**: extend `TrackEditor.ParseTest` to parse the Monte Rosa file (assert HR/cad
  present), fit a model, predict WWT, sanity-check finish time.

## Phases

- **P0** — `TrackPoint` fields + `GpxIo` extension parse + ParseTest assertions. *Foundation.*
- **P1** — `SignalCleaning` + `TrackResampler`.
- **P2** — `RaceAnalyzer` -> baseCurve + fatigue; `RaceModel` export/import. Validate on Olga file (console).
- **P3** — `RacePredictor` -> inject times, create copy. Validate: predict WWT, sanity finish time.
- **P4** — UI: Analyze + Apply windows, MainWindow wiring, signal checkboxes.
- **P5** — v2 factors: turns, roughness, altitude, surface range-tags, temp toggle.

Shippable after P4 (grade + fatigue + HR). P5 sharpens accuracy.

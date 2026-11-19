# Rolling Candle

A Quantower chart indicator that draws one live candle of the last N seconds' trades (10 by default), just right of
the latest bar.

It's a rolling window, not a fixed period. The open is the first trade still in the window, the high and low are the
extremes of the trades in it, the close is the latest trade, and the volume is their shares. As time passes, trades
older than the window drop out, so the candle rolls instead of starting fresh every 10 seconds. With nothing traded in
the window, it's flat at the last trade.

## What it shows

- **Brightness by volume against typical.** The candle's colour is as strong as its window's volume against the
  stock's typical volume for a window that long, averaged over the last few minutes: half strength at typical, full
  colour at twice typical, a quarter's opacity when quiet.
- **Where in the candle volume traded.** The body and wick are one smooth gradient. Each price is as bright as the
  shares traded there, and the busiest price gets the candle's full colour. Prices with fewer than 1,000 shares, or
  none, are at the lowest intensity, and so is the whole candle when the window has under 1,000 shares.
- **Who took each price.** Each price is green where it was bought (at or above the ask) and red where it was sold (at
  or below the bid), and in between by the share bought. Big selling inside a green candle shows red there. Prints
  between the bid and ask (the tape's grey ones) count for neither colour nor brightness, so they don't hide the
  volume that was bought or sold; they still move the candle's price and count in the volume under it. This reads
  the side from each print's `AggressorFlag`, so it needs a connection that sets it; on one that doesn't, every
  print counts as between and the candle stays at its lowest intensity.
- **Typical volume from the start.** When the candle starts, it reads the last few minutes of 10-second bars, so
  its brightness means something from the first trade rather than after 20 seconds and a jumpy few minutes. Bars
  don't say who took a trade, so their volume is scaled by the share bought or sold seen live (a half until about
  10,000 shares have traded); the bars fade out as live trading fills the lookback.
- **Bursts.** Past full colour (2 times typical volume) the busiest prices run hotter and hotter toward white, up to
  60% of the way at 16 times typical, so 3x, 5x and 10x look different. Beyond 4 times typical the body also gets a
  glow that grows with the burst, and the volume label turns bold.
- **Labels.** The window ("10s") above the candle and the window's volume under it.

Trades are timed on arrival, on the PC's clock, and rounded to their tick (a cent from $1, a hundredth of a cent below)
for the shading.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| Window (seconds) | 10 | How far back the candle reaches |
| Typical volume over the last (minutes) | 5 | How long typical volume is averaged over |
| Full colour at (times typical volume) | 2 | The volume for full colour |
| Glow beyond (times typical volume) | 4 | Where a burst's glow starts |
| Lowest intensity under (shares at a price, or in the window) | 1,000 | Prices, or windows, below this stay dim |
| Width (times the chart's candles) | 1.5 | How wide it's drawn |

## Build and install

Needs Quantower (built against v1.147.11, which runs on .NET 10) and the .NET 10 SDK on Windows.

```powershell
.\deploy.ps1
```

This builds `src\RollingCandle.csproj` against Quantower's installed `TradingPlatform.BusinessLayer.dll` and copies
`RollingCandle.dll` to `C:\Quantower\Settings\Scripts\Indicators\RollingCandle`. For a different Quantower version or
folder, build with `-p:QuantowerBin=<its bin folder>`. Then add **Rolling Candle** to a chart from the indicators list.

It's also part of [ScalpFeed](https://github.com/raghavsiyer/scalpfeed), which includes this repo as a submodule and
builds it into its Key Moving Averages indicator as well. With ScalpFeed installed, don't install this one too, or the
indicator shows twice in Quantower's list.

## Checks

The candle's maths doesn't need Quantower, so it's checked on its own:

```powershell
dotnet run --project tests
```

## Layout

- `src/RollingCandle.cs`: the rolling candle, typical-volume brightness and the shading by price and side. No
  Quantower.
- `src/RollingCandleView.cs`: draws it on a chart.
- `src/RollingCandleIndicator.cs`: the indicator and its settings.
- `tests/`: the checks.

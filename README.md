# RaymanHelper (cTrader / cAlgo)

## Trading assistant for cTrader (cAlgo Robot)

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Version](https://img.shields.io/badge/version-0.3-green.svg)]()


### Summary
- Simple robot to automatically manage SL/TP, break‑even and trailing stop.
- Displays useful information directly on the chart (balance, spread, parameter validation errors).
- Checks and adjusts existing orders, and closes positions if the market already hit the expected SL.


### Main features
- Automatic adjustment of Stop Loss and Take Profit for open positions.
- Break‑even: moves SL after a threshold is reached (BreakEvenTriggerPips).
- Trailing stop: follows price once the threshold is reached (TrailingStopPips).
- Spread control: refuses to act if spread exceeds MaxAllowedSpread and shows an alert.
- Chart info panel showing time, spread, balance, parameters and validation errors.
- Log filtering to reduce noise (similar-message detection).


### Exposed parameters (default values)
- Min Lot: 0.01
- Fixed Lot: 0.01
- Risk Per Trade %: 1 (range 0.1 — 2)
- Stop Loss (pips): 5
- Take Profit (pips): 5
- Trailing Stop (pips): 3
- Break-even Trigger (pips): 3
- Break-even Margin (pips): 1
- Max Allowed Spread (pips): 0.2


### Installation and usage
1. Open cTrader Automate (cAlgo).
2. Import `RaymanHelper.cs` into a Robot project.
3. Build the project.
4. Attach the robot to the chart of the desired symbol.
5. Adjust parameters in the robot UI and start.


### Implementation notes
- The robot keeps track of already-checked positions in `checkedPositions` to avoid repeated processing.
- UpdateChartInfo is called on every tick; consider throttling (e.g. once per second) to reduce overhead.
- ValidateParameters throws ArgumentException for inconsistent parameters; these errors are shown on the chart.


### Trailing stop & break-even behavior
- `Break-even Trigger`: when a position moves in your favor by this number of pips from the entry price, the bot will attempt to move the Stop Loss to lock profit. It sets the SL to the entry price plus `Break-even Margin` for BUY positions, or entry minus `Break-even Margin` for SELL positions.
- `Break-even Margin`: number of pips to add to the entry when moving SL to break-even. A positive margin places the SL into profit by that many pips (providing a buffer above/below entry).
- `Trailing Stop`: once conditions are met (typically after the break-even trigger), the bot follows the price and moves the SL so it remains `Trailing Stop` away from the current price (SL = currentPrice - `Trailing Stop` for BUY, SL = currentPrice + `Trailing Stop` for SELL).
- Notes: all pip math uses Symbol.PipSize and NormalizePrice to respect the instrument tick size. The bot only updates SL if the new value is an improvement (it avoids worsening an existing SL) and applies small epsilons to prevent frequent tiny modifications.


### Suggested improvements
- Add throttling for UpdateChartInfo.
- Allow StopLossPips == 0 and/or TakeProfitPips == 0 to disable either (adjust MinValue accordingly).
- Option to ignore positions from other symbols (multi-symbol handling).
- Centralize error and log management (levels, timestamps).


### Limitations / risks
- Works only on cTrader Automate (cAlgo) — verify API compatibility with your cTrader version.
- Automatic SL/TP modifications involve trading API calls — test on a demo account before using live.
- Does not replace human supervision: monitor logs and the chart error panel.


### Authors
- [@rayman223](https://www.github.com/rayman223)


### Contributing
- Contributions are welcome. Feel free to propose changes, open issues, push branches, and submit pull requests. Please include a short description of your changes when pushing or creating a PR.


### License
- MIT.
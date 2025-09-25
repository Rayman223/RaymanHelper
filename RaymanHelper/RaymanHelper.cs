//+------------------------------------------------------------------+
//| Helper cTrader - Version 0.1                                     |
//| By Rayman223                                                     |
//+------------------------------------------------------------------+

using System;
using System.Linq;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class RaymanHelper : Robot
    {
        // === STRATEGY PARAMETERS ===
        [Parameter("Min Lot", Group = "Money Management", DefaultValue = 1, MinValue = 0.01)]
        public double MinLotSize { get; set; }

        [Parameter("Fixed Lot", Group = "Money Management", DefaultValue = 1, MinValue = 0.01)]
        public double LotSize { get; set; }

        [Parameter("Use Dynamic Lot?", Group = "Money Management", DefaultValue = true)]
        public bool UseDynamicLot { get; set; }

        [Parameter("Risk Per Trade %", Group = "Money Management", DefaultValue = 1.8, MinValue = 0.1, MaxValue = 2)]
        public double RiskPercent { get; set; }

        [Parameter("Max open positions", Group = "SL/TP", DefaultValue = 4, MaxValue = 10, MinValue = 1, Step = 1)]
        public int MaxOpenPosition { get; set; }

        [Parameter("Stop Loss (pips)", Group = "SL/TP", DefaultValue = 9, MaxValue = 100, MinValue = 1, Step = 1)]
        public int StopLossPips { get; set; }

        [Parameter("Take Profit (pips)", Group = "SL/TP", DefaultValue = 26, MaxValue = 100, MinValue = 1, Step = 1)]
        public int TakeProfitPips { get; set; }

        // Nomber of pips for new SL after Break-even
        [Parameter("Trailing Stop (pips)", Group = "SL/TP", DefaultValue = 12, MaxValue = 100, MinValue = 1, Step = 1)]
        public int TrailingStopPips { get; set; }

        // Number of pips when new SL on price
        [Parameter("Break-even Trigger (pips)", Group = "SL/TP", DefaultValue = 7, MaxValue = 20, MinValue = 1, Step = 1)]
        public int BreakEvenTriggerPips { get; set; }
        // Margin add to the price for the new SL
        [Parameter("Break-even Margin (pips)", Group = "SL/TP", DefaultValue = 1, MinValue = 0, MaxValue = 100, Step = 0.1)]
        public int BreakEvenMarginPips { get; set; }

        [Parameter("Total loss", Group = "Risk", DefaultValue = 200, MaxValue = 1000, MinValue = 0, Step = 1)]
        public int MaxLoss { get; set; }

        [Parameter("Max Allowed Spread (pips)", Group = "Settings", DefaultValue = 0.4, MaxValue = 0.7, MinValue = 0, Step = 0.1)]
        public double MaxAllowedSpread { get; set; }

        [Parameter("Rollover Hour (UTC)", Group = "Settings", DefaultValue = 20, MinValue = 0, MaxValue = 23)]
        public int RolloverHour { get; set; }

        [Parameter("Close Before Weekend (hours)", Group = "Filters", DefaultValue = 1, MinValue = 0, MaxValue = 2, Step = 1)]
        public int CloseBeforeWeekendHours { get; set; }

        [Parameter("Enable TimeFrame 15M", Group = "Backtest Settings", DefaultValue = true)]
        public bool EnableTimeFrame15M { get; set; }

        [Parameter("Enable TimeFrame 30M", Group = "Backtest Settings", DefaultValue = true)]
        public bool EnableTimeFrame30M { get; set; }

        [Parameter("Enable TimeFrame 1H", Group = "Backtest Settings", DefaultValue = true)]
        public bool EnableTimeFrame1H { get; set; }

        // === END STRATEGY PARAMETERS ===

        // HashSet pour garder trace des positions déjà vérifiées
        private HashSet<int> checkedPositions = new HashSet<int>();

        private string lastLogMessage = string.Empty;
        private string lastLogSource = string.Empty;
        private double GlobalBalance()
        {
            return Math.Floor(History
                .Sum(h => h.NetProfit) * 100) / 100;
        }

        protected override void OnStart()
        {
            // Display market opening and closing hours
            DisplayMarketHours();

            // Validate parameters
            ValidateParameters();

            Log("Bot started successfully", "Info");
        }

        protected override void OnTick()
        {
            // Close positions before rollover
            ClosePositionsBeforeRollover();

            // Manage trailing stop and break-even adjustments
            ManageBreakEven();
            ManageTrailingStop();

            double price = Bars.ClosePrices.LastValue;
            double pricePrev = Bars.ClosePrices.Last(1);
            double GlobalBalanceValue = GlobalBalance();

            if (GlobalBalanceValue <= -MaxLoss)
            {
                Log($"Maximum loss reached. No more positions will be opened ({GlobalBalanceValue} €).", "Warning");
                // If no more positions are open, stop the robot
                if (Positions.Count(p => p.SymbolName == SymbolName) == 0)
                {
                    Log("Robot stopped due to maximum loss.", "Warning");
                    Stop();
                }
                return;
            }
        }



        private void VerifyPositions()
        {
            foreach (var position in Positions.FindAll(SymbolName))
            {
                // Vérifie si on a déjà traité cette position
                if (checkedPositions.Contains(position.Id))
                    continue;

                double? expectedSL = null;
                double? expectedTP = null;

                if (StopLossPips > 0)
                {
                    expectedSL = position.TradeType == TradeType.Buy
                        ? position.EntryPrice - StopLossPips * Symbol.PipSize
                        : position.EntryPrice + StopLossPips * Symbol.PipSize;
                }

                if (TakeProfitPips > 0)
                {
                    expectedTP = position.TradeType == TradeType.Buy
                        ? position.EntryPrice + TakeProfitPips * Symbol.PipSize
                        : position.EntryPrice - TakeProfitPips * Symbol.PipSize;
                }

                bool needsUpdate = false;
                double? newSL = position.StopLoss;
                double? newTP = position.TakeProfit;

                if (expectedSL.HasValue && (!position.StopLoss.HasValue || Math.Abs(position.StopLoss.Value - expectedSL.Value) > Symbol.PipSize))
                {
                    newSL = expectedSL;
                    needsUpdate = true;
                }

                if (expectedTP.HasValue && (!position.TakeProfit.HasValue || Math.Abs(position.TakeProfit.Value - expectedTP.Value) > Symbol.PipSize))
                {
                    newTP = expectedTP;
                    needsUpdate = true;
                }

                if (needsUpdate)
                {
                    Log($"Adjusted SL/TP for position {position.Id} | New SL={position.StopLoss.Value} -> {newSL} | New TP={position.TakeProfit.Value} -> {newTP}", "Info");

                    position.ModifyStopLossPrice(newSL);
                    position.ModifyTakeProfitPrice(newTP);
                }

                // Ajoute l'ID dans la liste des positions déjà vérifiées
                checkedPositions.Add(position.Id);
            }
        }


        private double GetDynamicVolume()
        {
            double riskAmount = Account.Balance * (RiskPercent / 100);
            double pipValue = Symbol.PipValue;

            if (StopLossPips <= 0 || pipValue <= 0 || MinLotSize <= 0)
                throw new ArgumentException("StopLossPips, PipValue or MinLotSize is invalid.");

            double volumeInLots = riskAmount / (StopLossPips * pipValue);
            volumeInLots = Math.Round(volumeInLots, 2);

            if (volumeInLots < MinLotSize)
            {
                Log($"Calculated volume ({volumeInLots}) is less than the minimal size lot ({MinLotSize}). Utilisation of the minimal size lot.", "Warning");
                volumeInLots = MinLotSize;
            }

            return Symbol.QuantityToVolumeInUnits(volumeInLots);
        }

        private void ManageBreakEven()
        {
            // epsilon to avoid frequent small adjustments
            double epsilon = Symbol.PipSize / 2;

            foreach (var position in Positions.FindAll(SymbolName))
            {
                double distance = position.TradeType == TradeType.Buy
                    ? Math.Round(Symbol.Bid - position.EntryPrice, 5)
                    : Math.Round(position.EntryPrice - Symbol.Ask, 5);

                if (distance >= BreakEvenTriggerPips * Symbol.PipSize)
                {
                    double newStopLoss = position.TradeType == TradeType.Buy
                        ? position.EntryPrice + (BreakEvenMarginPips * Symbol.PipSize)
                        : position.EntryPrice - (BreakEvenMarginPips * Symbol.PipSize) - GetSpreadInPips();
                    // Normalize the new stop loss price
                    newStopLoss = NormalizePrice(newStopLoss, position.TradeType);

                    if ((position.TradeType == TradeType.Buy && newStopLoss > position.StopLoss + epsilon) ||
                        (position.TradeType == TradeType.Sell && newStopLoss < position.StopLoss - epsilon))
                    {
                        Log($"Break-even adjusted | distance={distance} | New SL={newStopLoss} | Entry={position.EntryPrice}", "Info");
                        position.ModifyStopLossPrice(newStopLoss);
                    }
                }
            }
        }

        private void ManageTrailingStop()
        {
            // epsilon to avoid frequent small adjustments
            double epsilon = Symbol.PipSize / 2;

            foreach (var position in Positions.FindAll(SymbolName))
            {
                double currentPrice = position.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
                double breakEvenPrice = position.TradeType == TradeType.Buy
                    ? position.EntryPrice + BreakEvenMarginPips * Symbol.PipSize
                    : position.EntryPrice - (BreakEvenMarginPips * Symbol.PipSize) - Symbol.Spread;

                // Vérifie si le prix a dépassé le niveau de Break-even
                if ((position.TradeType == TradeType.Buy && currentPrice > breakEvenPrice) ||
                    (position.TradeType == TradeType.Sell && currentPrice < breakEvenPrice))
                {
                    // Calcule le nouveau Stop Loss pour rester TrailingStopPips pips en dessous du prix actuel
                    double newStopLoss = position.TradeType == TradeType.Buy
                        ? currentPrice - TrailingStopPips * Symbol.PipSize
                        : currentPrice + TrailingStopPips * Symbol.PipSize;
                    // Normalize the new stop loss price
                    newStopLoss = NormalizePrice(newStopLoss, position.TradeType);

                    double CurrentPips = Math.Round((newStopLoss - position.EntryPrice) / Symbol.PipSize, 2);
                    // Applique le nouveau Stop Loss uniquement s'il est plus favorable
                    if ((position.TradeType == TradeType.Buy && newStopLoss > position.StopLoss + epsilon && newStopLoss > breakEvenPrice) ||
                        (position.TradeType == TradeType.Sell && newStopLoss < position.StopLoss - epsilon && newStopLoss < breakEvenPrice))
                    {
                        Log($"Trailing Stop ajusted | New SL={newStopLoss} | Actual price={currentPrice} | Entry={position.EntryPrice} | Pips={CurrentPips}", "Info");
                        position.ModifyStopLossPrice(newStopLoss);
                    }
                }
            }
        }

        private bool StopOpenPositionsBeforeRollover()
        {
            TimeSpan currentTime = Server.Time.TimeOfDay;
            TimeSpan rolloverTime = new TimeSpan(RolloverHour, 0, 0); // Manual user hour (UTC)
            TimeSpan timeTillClose = Symbol.MarketHours.TimeTillClose(); // Automatic hour

            // Check if within 30 minutes of manual or automatic rollover time
            bool isNearManualRollover = currentTime >= rolloverTime - TimeSpan.FromMinutes(30) && currentTime < rolloverTime;
            bool isNearAutomaticRollover = timeTillClose <= TimeSpan.FromMinutes(30) && timeTillClose > TimeSpan.Zero;

            if (isNearManualRollover || isNearAutomaticRollover)
            {
                return true; // Prevent opening new positions
            }

            return false; // Allow opening new positions
        }

        private void ClosePositionsBeforeRollover()
        {
            /*
                Does not work properly in backtest mode
            */

            // Combine manual and automatic rollover checks
            TimeSpan rolloverTime = new TimeSpan(RolloverHour, 0, 0); // Manual user hour (UTC)
            TimeSpan currentTime = Server.Time.TimeOfDay;
            TimeSpan timeTillClose = Symbol.MarketHours.TimeTillClose(); // Automatic hour

            // Check if within 5 minutes of manual or automatic rollover time
            if ((currentTime >= rolloverTime - TimeSpan.FromMinutes(5) && currentTime <= rolloverTime) ||
            (timeTillClose <= TimeSpan.FromMinutes(5) && timeTillClose > TimeSpan.Zero))
            {
                Log($"Closing positions to avoid swap fees. Current time: {currentTime}, Rollover time: {rolloverTime}, Time till close: {timeTillClose}.", "Warning");

                // Close all positions opened by the bot
                foreach (var position in Positions.FindAll(SymbolName))
                {
                    ClosePosition(position);
                }
            }
        }

        private void ValidateParameters()
        {
            if (StopLossPips <= 0 || TakeProfitPips <= 0)
                throw new ArgumentException("Stop Loss and Take Profit must be greater than 0.");

            if (MaxOpenPosition <= 0)
                throw new ArgumentException("The maximum number of open positions must be greater than 0.");

            if (RiskPercent <= 0 || RiskPercent > 100)
                throw new ArgumentException("Risk Percent must be between 0 and 100.");
        }

        private void Log(string message, string level = "Info")
        {
            string logSource = new System.Diagnostics.StackTrace().GetFrame(1).GetMethod().Name;
            string formattedMessage = $"[{level}]: {message}";

            // Check if the message is similar to the last log (ignoring dynamic variables)
            if (lastLogMessage != null && AreMessagesSimilar(formattedMessage, lastLogMessage))
            {
                return;
            }

            // Save log and update last log
            lastLogMessage = formattedMessage;
            lastLogSource = logSource;
            Print(formattedMessage);
        }

        private bool AreMessagesSimilar(string currentMessage, string lastMessage)
        {
            // Ignore les parties dynamiques des messages (comme les valeurs numériques)
            string strippedCurrent = System.Text.RegularExpressions.Regex.Replace(currentMessage, @"\d+(\.\d+)?", "");
            string strippedLast = System.Text.RegularExpressions.Regex.Replace(lastMessage, @"\d+(\.\d+)?", "");

            return strippedCurrent == strippedLast;
        }

        private void DisplayMarketHours()
        {
            var TimeTillClose = Symbol.MarketHours.TimeTillClose();
            Log($"TimeTillClose() : {TimeTillClose}", "Info");
            Log($"Current server time : {Server.Time}", "Info");
            Log($"Time zone : {TimeZoneInfo.Local.StandardName}", "Info");
            Log($"Symbol: {SymbolName}", "Info");
        }

        private bool IsSpreadAcceptable()
        {
            double spreadInPips = GetSpreadInPips();

            if (spreadInPips > MaxAllowedSpread)
            {
                Log($"Spread too high : {spreadInPips:F2} pips (Max allow : {MaxAllowedSpread:F2} pips)", "Warning");
                return false;
            }

            return true;
        }

        private void CloseProfitablePositions()
        {
            foreach (var position in Positions.FindAll(SymbolName))
            {
                if (position.NetProfit > 0)
                {
                    ClosePosition(position);
                    Log($"position closed in profit : {position.TradeType} | {position.SymbolName} | {position.NetProfit:F2} €", "Info");
                }
            }
        }

        private double GetSpreadInPips()
        {
            // Symbol.Spread ?
            // make search because used with trailing stop
            return (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
        }

        // Normalize price to the nearest tick size
        private double NormalizePrice(double price, TradeType tradeType)
        {
            double tickSize = Symbol.TickSize;
            double normalized;

            if (tradeType == TradeType.Buy)
                normalized = Math.Floor(price / tickSize) * tickSize;   // Buy
            else
                normalized = Math.Ceiling(price / tickSize) * tickSize; // Sell

            // arrondi final au bon nombre de décimales du symbole
            return Math.Round(normalized, Symbol.Digits);
        }
    }
}

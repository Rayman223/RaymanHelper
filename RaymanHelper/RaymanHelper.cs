//+------------------------------------------------------------------+
//| Helper cTrader - Version 0.2                                     |
//| By Rayman223                                                     |
//+------------------------------------------------------------------+

using System;
using System.Linq;
using System.Collections.Generic;
using cAlgo.Indicators;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class RaymanHelper : Robot
    {
        // === STRATEGY PARAMETERS ===
        [Parameter("Min Lot", Group = "Money Management", DefaultValue = 0.01, MinValue = 0.01)]
        public double MinLotSize { get; set; }

        [Parameter("Fixed Lot", Group = "Money Management", DefaultValue = 0.01, MinValue = 0.01)]
        public double LotSize { get; set; }

        [Parameter("Use Dynamic Lot?", Group = "Money Management", DefaultValue = true)]
        public bool UseDynamicLot { get; set; }

        [Parameter("Risk Per Trade %", Group = "Money Management", DefaultValue = 1, MinValue = 0.1, MaxValue = 2)]
        public double RiskPercent { get; set; }

        [Parameter("Stop Loss (pips)", Group = "SL/TP", DefaultValue = 5, MaxValue = 1000, MinValue = 1, Step = 1)]
        public int StopLossPips { get; set; }

        [Parameter("Take Profit (pips)", Group = "SL/TP", DefaultValue = 5, MaxValue = 1000, MinValue = 1, Step = 1)]
        public int TakeProfitPips { get; set; }

        // Nomber of pips for new SL after Break-even
        [Parameter("Trailing Stop (pips)", Group = "SL/TP", DefaultValue = 3, MinValue = 1, Step = 1)]
        public int TrailingStopPips { get; set; }

        // Number of pips when new SL on price
        [Parameter("Break-even Trigger (pips)", Group = "SL/TP", DefaultValue = 2.9, MinValue = 1, Step = 1)]
        public int BreakEvenTriggerPips { get; set; }
        // Margin add to the price for the new SL
        [Parameter("Break-even Margin (pips)", Group = "SL/TP", DefaultValue = 0.9, MinValue = 0, Step = 0.1)]
        public int BreakEvenMarginPips { get; set; }

        [Parameter("Total loss", Group = "Risk", DefaultValue = 200, MaxValue = 1000, MinValue = 0, Step = 1)]
        public int MaxLoss { get; set; }

        [Parameter("Max Allowed Spread (pips)", Group = "Settings", DefaultValue = 0.2, MaxValue = 300, MinValue = 0, Step = 0.1)]
        public double MaxAllowedSpread { get; set; }

        [Parameter("Rollover Hour (UTC)", Group = "Settings", DefaultValue = 20, MinValue = 0, MaxValue = 23)]
        public int RolloverHour { get; set; }

        [Parameter("Close Before Weekend (hours)", Group = "Filters", DefaultValue = 1, MinValue = 0, MaxValue = 2, Step = 1)]
        public int CloseBeforeWeekendHours { get; set; }

        // === END STRATEGY PARAMETERS ===

        // HashSet pour garder trace des positions déjà vérifiées
        private HashSet<int> checkedPositions = new HashSet<int>();

        private RaymanHelperIndicator _helper;

        private string lastLogMessage = string.Empty;
        private string lastLogSource = string.Empty;
        private double GlobalBalance()
        {
            return Math.Floor(Account.Equity * 100) / 100;
        }

        protected override void OnStart()
        {
            _helper = Indicators.GetIndicator<RaymanHelperIndicator>(
                MinLotSize,   // MinLotSize
                RiskPercent,    // RiskPercent
                StopLossPips,      // StopLossPips
                MaxAllowedSpread     // MaxAllowedSpread
            );

            // Display market opening and closing hours
            DisplayMarketHours();

            // Validate parameters
            ValidateParameters();

            // s'abonner à l'événement de fermeture de position
            Positions.Closed += PositionsOnClosed;

            Log("Bot started successfully", "Info");
        }

        protected override void OnStop()
        {
            // se désabonner pour éviter les fuites / doubles appels
            Positions.Closed -= PositionsOnClosed;
        }

        protected override void OnTick()
        {
            // Close positions before rollover
            ClosePositionsBeforeRollover();

            // Check new positions
            VerifyPositions();

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
            double epsilon = Symbol.PipSize; // tolérance pour les petits écarts d'arrondi

            // Parcourt toutes les positions ouvertes pour le symbole courant
            foreach (var position in Positions.Where(p => p.SymbolName == SymbolName))
            {
                // Log sûr : on évite d'accéder à .Value s'il est null
                string currentSL = position.StopLoss.HasValue ? position.StopLoss.Value.ToString("F5") : "null";
                string currentTP = position.TakeProfit.HasValue ? position.TakeProfit.Value.ToString("F5") : "null";

                // si déjà traité, on skip
                if (checkedPositions.Contains(position.Id))
                    continue;

                // calcule SL/TP attendus (nullable si StopLossPips/TakeProfitPips <= 0)
                double? expectedSL = null;
                double? expectedTP = null;

                if (StopLossPips > 0)
                {
                    expectedSL = position.TradeType == TradeType.Buy
                        ? position.EntryPrice - StopLossPips * Symbol.PipSize
                        : position.EntryPrice + StopLossPips * Symbol.PipSize;

                    expectedSL = NormalizePrice(expectedSL.Value, position.TradeType);
                }

                if (TakeProfitPips > 0)
                {
                    expectedTP = position.TradeType == TradeType.Buy
                        ? position.EntryPrice + TakeProfitPips * Symbol.PipSize
                        : position.EntryPrice - TakeProfitPips * Symbol.PipSize;

                    expectedTP = NormalizePrice(expectedTP.Value, position.TradeType);
                }


                // Sécurité : fermer si le prix a déjà touché/excédé le SL
                bool closePosition = false;
                if (expectedSL.HasValue)
                {
                    if ((position.TradeType == TradeType.Buy && Symbol.Bid <= expectedSL.Value) ||
                        (position.TradeType == TradeType.Sell && Symbol.Ask >= expectedSL.Value))
                    {
                        closePosition = true;
                    }
                }

                if (closePosition)
                {
                    try
                    {
                        ClosePosition(position);
                        Log($"Position {position.Id} closed immediately because market already reached expected SL {expectedSL.Value:F5}", "Warning");
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to close position {position.Id}. Error: {ex.Message}", "Error");
                    }

                    checkedPositions.Add(position.Id);
                    continue; // passe à la position suivante
                }


                // détermine s'il faut mettre à jour SL et/ou TP
                bool updateSL = expectedSL.HasValue &&
                                (!position.StopLoss.HasValue || Math.Abs(position.StopLoss.Value - expectedSL.Value) > epsilon);

                bool updateTP = expectedTP.HasValue &&
                                (!position.TakeProfit.HasValue || Math.Abs(position.TakeProfit.Value - expectedTP.Value) > epsilon);

                if (updateSL || updateTP)
                {
                    string newSLStr = updateSL ? expectedSL.Value.ToString("F5") : currentSL;
                    string newTPStr = updateTP ? expectedTP.Value.ToString("F5") : currentTP;

                    Log($"Adjusted SL/TP for position {position.Id} | SL {currentSL} -> {newSLStr} | TP {currentTP} -> {newTPStr}", "Info");

                    try
                    {
                        if (updateSL)
                            position.ModifyStopLossPrice(expectedSL.Value); // on passe une valeur non-null

                        if (updateTP)
                            position.ModifyTakeProfitPrice(expectedTP.Value); // on passe une valeur non-null
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to modify SL/TP for position {position.Id}. Error: {ex.Message}", "Error");
                    }
                }

                // marque la position comme traitée (une seule vérification)
                checkedPositions.Add(position.Id);
            }
        }

        // Handler appelé à chaque position fermée
        private void PositionsOnClosed(PositionClosedEventArgs args)
        {
            var pos = args?.Position;
            if (pos == null)
                return;

            // retire l'id si présent (Remove renvoie true si supprimé)
            if (checkedPositions.Remove(pos.Id))
            {
                Log($"Position {pos.Id} fermée — retirée de checkedPositions.", "Info");
            }
            else
            {
                // optionnel : log si l'id n'était pas dans le set
                Log($"Position {pos.Id} fermée — id non présent dans checkedPositions.", "Debug");
            }
        }




        private void ManageBreakEven()
        {
            // epsilon to avoid frequent small adjustments
            double epsilon = Symbol.PipSize / 2;

            foreach (var position in Positions.Where(p => p.SymbolName == SymbolName))
            {
                double distance = position.TradeType == TradeType.Buy
                    ? NormalizePrice(Symbol.Bid - position.EntryPrice, position.TradeType)
                    : NormalizePrice(position.EntryPrice - Symbol.Ask, position.TradeType);

                if (distance >= BreakEvenTriggerPips * Symbol.PipSize)
                {
                    double newStopLoss = position.TradeType == TradeType.Buy
                        ? position.EntryPrice + (BreakEvenMarginPips * Symbol.PipSize)
                        : position.EntryPrice - (BreakEvenMarginPips * Symbol.PipSize); //- GetSpreadInPips() ??
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

            foreach (var position in Positions.Where(p => p.SymbolName == SymbolName))
            {
                double currentPrice = position.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
                double breakEvenPrice = position.TradeType == TradeType.Buy
                    ? position.EntryPrice + (BreakEvenMarginPips * Symbol.PipSize)
                    : position.EntryPrice - (BreakEvenMarginPips * Symbol.PipSize);

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
                foreach (var position in Positions.Where(p => p.SymbolName == SymbolName))
                {
                    ClosePosition(position);
                }
            }
        }

        private void ValidateParameters()
        {
            if (StopLossPips <= 0 || TakeProfitPips <= 0)
                throw new ArgumentException("Stop Loss and Take Profit must be greater than 0.");

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
            foreach (var position in Positions.Where(p => p.SymbolName == SymbolName))
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

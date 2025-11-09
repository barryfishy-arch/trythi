#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class EMA34Bounce_M1_StochasticEntry_Tracker : Strategy
    {
        #region Variables

        private string instrumentName = "";
        private string csvFilePath = "";
        private StreamWriter csvWriter = null;

        // M30 indicators for bounce detection
        private EMA ema34High_M30;
        private EMA ema34Low_M30;
        private SMA sma5Typical_M30;
        private ATR atr_M30;

        // M1 indicators for entry signals
        private Stochastics stoch_M1;
        private SMA sma50_M1;

        // M3 indicators for filter
        private Stochastics stoch_M3;
        private MACD macd_M3;

        // M30 bounce state tracking
        private Dictionary<string, M30BounceData> activeBounces = new Dictionary<string, M30BounceData>();

        // M30 Setup tracking (LONG)
        private int barsAboveEMAHigh_M30 = 0;
        private bool isInLongPullbackPhase_M30 = false;
        private bool hasEMAHighTouched_M30 = false;
        private double longExtremePrice_M30 = 0;
        private int longHighestBarIndex_M30 = 0;

        // M30 Setup tracking (SHORT)
        private int barsBelowEMALow_M30 = 0;
        private bool isInShortPullbackPhase_M30 = false;
        private bool hasEMALowTouched_M30 = false;
        private double shortExtremePrice_M30 = double.MaxValue;
        private int shortLowestBarIndex_M30 = 0;

        // M30 Extreme break handling flags
        private bool longTriggerAttempted_M30 = false;
        private bool shortTriggerAttempted_M30 = false;

        private int bounceCounter = 0;

        #endregion

        #region M30 Bounce Detection Classes

        private class M30BounceData
        {
            public string BounceID;
            public string Direction;
            public DateTime M30BounceTime;
            public double M30BouncePrice;
            public int M30BounceBar;
            public int M1StartBar; // M1 bar when bounce detected
            public int M1BarsTracked;
            public bool TrackingComplete;

            // Entry signals collected during this bounce
            public List<EntrySignalData> EntrySignals = new List<EntrySignalData>();
        }

        private class EntrySignalData
        {
            public int SignalNumber; // 1st, 2nd, 3rd signal during this bounce
            public int M1EntryBar;
            public DateTime M1EntryTime;
            public double M1EntryPrice;

            // M1 Stochastic values at entry
            public double M1_StochK_Entry;
            public double M1_StochD_Entry;
            public double M1_StochK_Prev;
            public double M1_StochD_Prev;

            // Retracement metrics at entry
            public double InitialPeak; // Peak before this entry
            public double RetracementDepth; // How far price pulled back
            public double RetracementPct; // Retracement as % of thrust

            // Filter states at entry
            public bool Filter_D_Below20;
            public bool Filter_M1_SMA50_Up;
            public bool Filter_M3_Stoch_Up;
            public bool Filter_M3_MACD_Up;
            public bool Filter_M3_Combined;

            // M3 indicator values at entry
            public double M3_StochK;
            public double M3_StochD;
            public double M3_MACD;
            public double M3_MACD_Prev;
            public double M3_StochK_Prev;

            // M1 50SMA values
            public double M1_SMA50_Current;
            public double M1_SMA50_Prev;

            // Stop loss calculation
            public double StopLossPrice; // Entry bar low/high + buffer
            public double StopDistance; // Pips from entry

            // Outcome tracking
            public bool StopHit;
            public int BarsToStop;
            public double MAE_AfterEntry; // Worst adverse move after entry
            public double MFE_AfterEntry; // Best favorable move after entry
            public bool Reached10Pips;
            public bool Reached20Pips;
            public bool Reached30Pips;
            public int BarsTo10Pips;
            public int BarsTo20Pips;
            public int BarsTo30Pips;
        }

        #endregion

        #region Properties

        [Display(Name = "Min Bars Above EMA (M30)", Description = "Minimum bars in uptrend before pullback", Order = 1, GroupName = "M30 Parameters")]
        public int MinBarsAboveEMA { get; set; }

        [Display(Name = "Min Bars To Trigger (M30)", Description = "Minimum bars since highest/lowest to trigger", Order = 2, GroupName = "M30 Parameters")]
        public int MinBarsToTrigger { get; set; }

        [Display(Name = "ATR Buffer Percent (M30)", Description = "ATR buffer percentage for entry validation", Order = 3, GroupName = "M30 Parameters")]
        public double ATRBufferPercent { get; set; }

        [Display(Name = "M1 Bars To Track", Description = "Number of M1 bars to track after M30 bounce", Order = 1, GroupName = "M1 Tracking")]
        public int M1BarsToTrack { get; set; }

        [Display(Name = "Stop Buffer Pips", Description = "Buffer pips to add beyond entry bar low/high for stop", Order = 2, GroupName = "M1 Tracking")]
        public double StopBufferPips { get; set; }

        #endregion

        #region Lifecycle

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "M1 Stochastic Entry Tracker for M30 EMA34 Bounces";
                Name = "EMA34Bounce_M1_StochasticEntry_Tracker";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 0;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 50;
                IsInstantiatedOnEachOptimizationIteration = true;

                // Default parameter values
                MinBarsAboveEMA = 3;
                MinBarsToTrigger = 1;
                ATRBufferPercent = 30.0;
                M1BarsToTrack = 150; // 2.5 hours
                StopBufferPips = 2; // 2 pip buffer
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 30); // BarsArray[1] = M30
                AddDataSeries(BarsPeriodType.Minute, 3);  // BarsArray[2] = M3
            }
            else if (State == State.DataLoaded)
            {
                // M30 indicators for bounce detection (BarsArray[1])
                ema34High_M30 = EMA(Highs[1], 34);
                ema34Low_M30 = EMA(Lows[1], 34);
                sma5Typical_M30 = SMA(Typicals[1], 5);
                atr_M30 = ATR(BarsArray[1], 14);

                // M1 indicators for entry signals (BarsArray[0])
                stoch_M1 = Stochastics(BarsArray[0], 3, 21, 3);  // K=21, D=3, smooth=3
                sma50_M1 = SMA(Closes[0], 50);

                // M3 indicators for filters (BarsArray[2])
                stoch_M3 = Stochastics(BarsArray[2], 3, 5, 2);
                macd_M3 = MACD(BarsArray[2], 5, 20, 30);

                // Initialize CSV file
                instrumentName = Instrument.FullName.Replace(" ", "").Replace("/", "");
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                csvFilePath = Path.Combine(
                    NinjaTrader.Core.Globals.UserDataDir,
                    "bin", "Custom",
                    $"EMA34_M1_Stochastic_Entries_{timestamp}.csv"
                );

                csvWriter = new StreamWriter(csvFilePath, false);
                csvWriter.WriteLine("BounceID,Direction,M30BounceTime,M30BouncePrice," +
                    "SignalNumber,M1EntryTime,M1EntryPrice," +
                    "M1_StochK_Entry,M1_StochD_Entry,M1_StochK_Prev,M1_StochD_Prev," +
                    "InitialPeak,RetracementDepth,RetracementPct," +
                    "Filter_D_Below20,Filter_M1_SMA50_Up,Filter_M3_Stoch_Up,Filter_M3_MACD_Up,Filter_M3_Combined," +
                    "M3_StochK,M3_StochK_Prev,M3_MACD,M3_MACD_Prev," +
                    "M1_SMA50_Current,M1_SMA50_Prev," +
                    "StopLossPrice,StopDistance," +
                    "StopHit,BarsToStop,MAE_AfterEntry,MFE_AfterEntry," +
                    "Reached10Pips,Reached20Pips,Reached30Pips," +
                    "BarsTo10Pips,BarsTo20Pips,BarsTo30Pips");

                Print($"═══════════════════════════════════════════════════");
                Print($"  EMA34 M1 STOCHASTIC ENTRY TRACKER INITIALIZED");
                Print($"═══════════════════════════════════════════════════");
                Print($"  Instrument: {instrumentName}");
                Print($"  M1 Bars To Track: {M1BarsToTrack}");
                Print($"  Stop Buffer: {StopBufferPips} pips");
                Print($"  CSV Output: {csvFilePath}");
                Print($"═══════════════════════════════════════════════════");
            }
            else if (State == State.Terminated)
            {
                // Write remaining bounces and close
                if (csvWriter != null)
                {
                    foreach (var kvp in activeBounces)
                    {
                        WriteAllEntrySignals(kvp.Key, kvp.Value);
                    }

                    csvWriter.Close();
                    Print($"✅ CSV file saved: {csvFilePath}");
                }
            }
        }

        #endregion

        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < 50 || CurrentBars[1] < 50 || CurrentBars[2] < 50)
                return;

            // Process M30 bars for bounce detection
            if (BarsInProgress == 1)
            {
                CheckM30LongSetup();
                CheckM30ShortSetup();
            }

            // Process M1 bars for entry signals
            if (BarsInProgress == 0)
            {
                TrackActiveBounces_M1();
            }
        }

        #endregion

        #region M30 Bounce Detection

        private void CheckM30LongSetup()
        {
            double currentClose = Closes[1][0];
            double currentHigh = Highs[1][0];
            double currentLow = Lows[1][0];
            double currentEMA34High = ema34High_M30[0];
            double currentSMA5 = sma5Typical_M30[0];

            // Phase 1: Uptrend detection
            if (currentClose > currentEMA34High)
            {
                barsAboveEMAHigh_M30++;

                if (currentHigh > longExtremePrice_M30)
                {
                    longExtremePrice_M30 = currentHigh;
                    longHighestBarIndex_M30 = CurrentBars[1];
                }

                if (barsAboveEMAHigh_M30 >= MinBarsAboveEMA && !isInLongPullbackPhase_M30)
                {
                    isInLongPullbackPhase_M30 = true;
                }
            }
            else
            {
                barsAboveEMAHigh_M30 = 0;
                longExtremePrice_M30 = 0;
                isInLongPullbackPhase_M30 = false;
                hasEMAHighTouched_M30 = false;
            }

            // Phase 2: EMA touch
            if (isInLongPullbackPhase_M30)
            {
                if (currentLow <= currentEMA34High && currentClose >= currentEMA34High)
                {
                    hasEMAHighTouched_M30 = true;
                }

                // Extreme break handling during pullback
                if (currentHigh > longExtremePrice_M30)
                {
                    bool shouldResetToPhase1 = false;

                    if (!longTriggerAttempted_M30)
                    {
                        // No trigger attempted yet - reset to Phase 1 with new extreme
                        shouldResetToPhase1 = true;
                    }
                    else
                    {
                        // Trigger already attempted - just update extreme, don't reset
                        // This allows continuation patterns after failed triggers
                        shouldResetToPhase1 = false;
                    }

                    if (shouldResetToPhase1)
                    {
                        // Reset to Phase 1: new uptrend with higher high
                        isInLongPullbackPhase_M30 = false;
                        hasEMAHighTouched_M30 = false;
                        longTriggerAttempted_M30 = false;
                        longExtremePrice_M30 = currentHigh;
                        longHighestBarIndex_M30 = CurrentBars[1];
                    }
                    else
                    {
                        // Just update the extreme, stay in pullback phase
                        longExtremePrice_M30 = currentHigh;
                        longHighestBarIndex_M30 = CurrentBars[1];
                    }
                }
            }

            // Phase 3: Entry trigger - CREATE M1 TRACKING
            if (isInLongPullbackPhase_M30 && hasEMAHighTouched_M30)
            {
                double atrBuffer = atr_M30[0] * (ATRBufferPercent / 100.0);
                bool aboveSMA5 = currentClose > (currentSMA5 + atrBuffer);
                bool aboveWave = currentClose > (currentEMA34High + atrBuffer);
                int barsSinceHighest = CurrentBars[1] - longHighestBarIndex_M30;

                if (barsSinceHighest < 0 || barsSinceHighest > 10000)
                {
                    longHighestBarIndex_M30 = CurrentBars[1];
                    barsSinceHighest = 0;
                }

                bool minBarsCheck = barsSinceHighest >= MinBarsToTrigger;

                if (aboveSMA5 && aboveWave && minBarsCheck)
                {
                    // Mark that we're attempting a trigger for this pullback
                    longTriggerAttempted_M30 = true;

                    bounceCounter++;
                    string bounceID = $"LONG_{bounceCounter}";

                    M30BounceData bounce = new M30BounceData
                    {
                        BounceID = bounceID,
                        Direction = "LONG",
                        M30BounceTime = Times[1][0],
                        M30BouncePrice = currentClose,
                        M30BounceBar = CurrentBars[1],
                        M1StartBar = CurrentBars[0],
                        M1BarsTracked = 0,
                        TrackingComplete = false
                    };

                    activeBounces[bounceID] = bounce;

                    Print($"✅ [{bounceID}] M30 LONG Bounce @ {bounce.M30BounceTime:HH:mm} Price: {bounce.M30BouncePrice:F5}");

                    // Draw triangle on chart for visual verification
                    Draw.TriangleUp(this, "LongBounce_" + bounceCounter, false, Times[1][0], Lows[1][0] - (5 * TickSize), Brushes.Green);

                    // Reset ALL state variables for next setup
                    barsAboveEMAHigh_M30 = 0;
                    isInLongPullbackPhase_M30 = false;
                    hasEMAHighTouched_M30 = false;
                    longExtremePrice_M30 = 0;
                    longHighestBarIndex_M30 = 0;
                    longTriggerAttempted_M30 = false;
                }
            }
        }

        private void CheckM30ShortSetup()
        {
            double currentClose = Closes[1][0];
            double currentHigh = Highs[1][0];
            double currentLow = Lows[1][0];
            double currentEMA34Low = ema34Low_M30[0];
            double currentSMA5 = sma5Typical_M30[0];

            // Phase 1: Downtrend detection
            if (currentClose < currentEMA34Low)
            {
                barsBelowEMALow_M30++;

                if (currentLow < shortExtremePrice_M30)
                {
                    shortExtremePrice_M30 = currentLow;
                    shortLowestBarIndex_M30 = CurrentBars[1];
                }

                if (barsBelowEMALow_M30 >= MinBarsAboveEMA && !isInShortPullbackPhase_M30)
                {
                    isInShortPullbackPhase_M30 = true;
                }
            }
            else
            {
                barsBelowEMALow_M30 = 0;
                shortExtremePrice_M30 = double.MaxValue;
                isInShortPullbackPhase_M30 = false;
                hasEMALowTouched_M30 = false;
            }

            // Phase 2: EMA touch
            if (isInShortPullbackPhase_M30)
            {
                if (currentHigh >= currentEMA34Low && currentClose <= currentEMA34Low)
                {
                    hasEMALowTouched_M30 = true;
                }

                // Extreme break handling during pullback
                if (currentLow < shortExtremePrice_M30)
                {
                    bool shouldResetToPhase1 = false;

                    if (!shortTriggerAttempted_M30)
                    {
                        // No trigger attempted yet - reset to Phase 1 with new extreme
                        shouldResetToPhase1 = true;
                    }
                    else
                    {
                        // Trigger already attempted - just update extreme, don't reset
                        // This allows continuation patterns after failed triggers
                        shouldResetToPhase1 = false;
                    }

                    if (shouldResetToPhase1)
                    {
                        // Reset to Phase 1: new downtrend with lower low
                        isInShortPullbackPhase_M30 = false;
                        hasEMALowTouched_M30 = false;
                        shortTriggerAttempted_M30 = false;
                        shortExtremePrice_M30 = currentLow;
                        shortLowestBarIndex_M30 = CurrentBars[1];
                    }
                    else
                    {
                        // Just update the extreme, stay in pullback phase
                        shortExtremePrice_M30 = currentLow;
                        shortLowestBarIndex_M30 = CurrentBars[1];
                    }
                }
            }

            // Phase 3: Entry trigger - CREATE M1 TRACKING
            if (isInShortPullbackPhase_M30 && hasEMALowTouched_M30)
            {
                double atrBuffer = atr_M30[0] * (ATRBufferPercent / 100.0);
                bool belowSMA5 = currentClose < (currentSMA5 - atrBuffer);
                bool belowWave = currentClose < (currentEMA34Low - atrBuffer);
                int barsSinceLowest = CurrentBars[1] - shortLowestBarIndex_M30;

                if (barsSinceLowest < 0 || barsSinceLowest > 10000)
                {
                    shortLowestBarIndex_M30 = CurrentBars[1];
                    barsSinceLowest = 0;
                }

                bool minBarsCheck = barsSinceLowest >= MinBarsToTrigger;

                if (belowSMA5 && belowWave && minBarsCheck)
                {
                    // Mark that we're attempting a trigger for this pullback
                    shortTriggerAttempted_M30 = true;

                    bounceCounter++;
                    string bounceID = $"SHORT_{bounceCounter}";

                    M30BounceData bounce = new M30BounceData
                    {
                        BounceID = bounceID,
                        Direction = "SHORT",
                        M30BounceTime = Times[1][0],
                        M30BouncePrice = currentClose,
                        M30BounceBar = CurrentBars[1],
                        M1StartBar = CurrentBars[0],
                        M1BarsTracked = 0,
                        TrackingComplete = false
                    };

                    activeBounces[bounceID] = bounce;

                    Print($"✅ [{bounceID}] M30 SHORT Bounce @ {bounce.M30BounceTime:HH:mm} Price: {bounce.M30BouncePrice:F5}");

                    // Draw triangle on chart for visual verification
                    Draw.TriangleDown(this, "ShortBounce_" + bounceCounter, false, Times[1][0], Highs[1][0] + (5 * TickSize), Brushes.Red);

                    // Reset ALL state variables for next setup
                    barsBelowEMALow_M30 = 0;
                    isInShortPullbackPhase_M30 = false;
                    hasEMALowTouched_M30 = false;
                    shortExtremePrice_M30 = double.MaxValue;
                    shortLowestBarIndex_M30 = 0;
                    shortTriggerAttempted_M30 = false;
                }
            }
        }

        #endregion

        #region M1 Entry Tracking

        private void TrackActiveBounces_M1()
        {
            List<string> completedBounces = new List<string>();

            foreach (var kvp in activeBounces)
            {
                string bounceID = kvp.Key;
                M30BounceData bounce = kvp.Value;

                if (bounce.TrackingComplete)
                    continue;

                bounce.M1BarsTracked++;

                // Detect M1 stochastic K cross D entry signals
                DetectStochasticEntryCriteria(bounce);

                // Update existing entry signals outcomes
                UpdateEntrySignalOutcomes(bounce);

                // Check if tracking complete
                if (bounce.M1BarsTracked >= M1BarsToTrack)
                {
                    bounce.TrackingComplete = true;
                    completedBounces.Add(bounceID);
                    WriteAllEntrySignals(bounceID, bounce);
                }
            }

            // Remove completed bounces
            foreach (string bounceID in completedBounces)
            {
                activeBounces.Remove(bounceID);
            }
        }

        private void DetectStochasticEntryCriteria(M30BounceData bounce)
        {
            double currentStochK = stoch_M1.K[0];
            double currentStochD = stoch_M1.D[0];
            double prevStochK = stoch_M1.K[1];
            double prevStochD = stoch_M1.D[1];

            bool isLong = bounce.Direction == "LONG";

            // Check for K cross D signal
            bool kCrossDSignal = false;

            if (isLong)
            {
                // LONG: K crosses above D (and D was below 50)
                if (currentStochK > currentStochD && prevStochK <= prevStochD && prevStochD < 50)
                {
                    kCrossDSignal = true;
                }
            }
            else
            {
                // SHORT: K crosses below D (and D was above 50)
                if (currentStochK < currentStochD && prevStochK >= prevStochD && prevStochD > 50)
                {
                    kCrossDSignal = true;
                }
            }

            if (!kCrossDSignal)
                return;

            // We have a K cross D signal - create entry signal data

            int signalNumber = bounce.EntrySignals.Count + 1;

            EntrySignalData signal = new EntrySignalData
            {
                SignalNumber = signalNumber,
                M1EntryBar = CurrentBars[0],
                M1EntryTime = Times[0][0],
                M1EntryPrice = Closes[0][0],
                M1_StochK_Entry = currentStochK,
                M1_StochD_Entry = currentStochD,
                M1_StochK_Prev = prevStochK,
                M1_StochD_Prev = prevStochD
            };

            // Calculate retracement metrics
            CalculateRetracementMetrics(bounce, signal);

            // Evaluate all filters
            EvaluateFilters(bounce, signal);

            // Calculate stop loss
            CalculateStopLoss(bounce, signal);

            bounce.EntrySignals.Add(signal);

            Print($"   📊 [{bounce.BounceID}] Signal #{signalNumber} @ {signal.M1EntryTime:HH:mm:ss} - K:{currentStochK:F1} D:{currentStochD:F1} Entry:{signal.M1EntryPrice:F5}");
        }

        private void CalculateRetracementMetrics(M30BounceData bounce, EntrySignalData signal)
        {
            // Find initial peak (highest high or lowest low since bounce)
            bool isLong = bounce.Direction == "LONG";
            double extremePrice = bounce.M30BouncePrice;

            int barsBack = CurrentBars[0] - bounce.M1StartBar;

            for (int i = 0; i <= barsBack && i < CurrentBars[0]; i++)
            {
                if (isLong)
                {
                    if (Highs[0][i] > extremePrice)
                        extremePrice = Highs[0][i];
                }
                else
                {
                    if (Lows[0][i] < extremePrice)
                        extremePrice = Lows[0][i];
                }
            }

            signal.InitialPeak = extremePrice;

            // Calculate retracement
            double currentPrice = signal.M1EntryPrice;

            if (isLong)
            {
                double thrust = extremePrice - bounce.M30BouncePrice;
                signal.RetracementDepth = extremePrice - currentPrice;
                signal.RetracementPct = thrust > 0 ? (signal.RetracementDepth / thrust) * 100 : 0;
            }
            else
            {
                double thrust = bounce.M30BouncePrice - extremePrice;
                signal.RetracementDepth = currentPrice - extremePrice;
                signal.RetracementPct = thrust > 0 ? (signal.RetracementDepth / thrust) * 100 : 0;
            }
        }

        private void EvaluateFilters(M30BounceData bounce, EntrySignalData signal)
        {
            bool isLong = bounce.Direction == "LONG";

            // Filter 1: D < 20 for longs, D > 80 for shorts
            if (isLong)
                signal.Filter_D_Below20 = signal.M1_StochD_Prev < 20;
            else
                signal.Filter_D_Below20 = signal.M1_StochD_Prev > 80;

            // Filter 2: M1 50SMA angled in direction
            signal.M1_SMA50_Current = sma50_M1[0];
            signal.M1_SMA50_Prev = sma50_M1[1];

            if (isLong)
                signal.Filter_M1_SMA50_Up = signal.M1_SMA50_Current > signal.M1_SMA50_Prev;
            else
                signal.Filter_M1_SMA50_Up = signal.M1_SMA50_Current < signal.M1_SMA50_Prev;

            // Filter 3: M3 Stochastic and MACD alignment
            signal.M3_StochK = stoch_M3.K[0];
            signal.M3_StochK_Prev = stoch_M3.K[1];
            signal.M3_MACD = macd_M3[0];
            signal.M3_MACD_Prev = macd_M3[1];

            if (isLong)
            {
                signal.Filter_M3_Stoch_Up = signal.M3_StochK > signal.M3_StochK_Prev;
                signal.Filter_M3_MACD_Up = signal.M3_MACD > signal.M3_MACD_Prev;
            }
            else
            {
                signal.Filter_M3_Stoch_Up = signal.M3_StochK < signal.M3_StochK_Prev;
                signal.Filter_M3_MACD_Up = signal.M3_MACD < signal.M3_MACD_Prev;
            }

            // Waive stochastic if extreme (from TrendContinuation logic)
            if (signal.M3_StochK > 80 || signal.M3_StochK < 20)
            {
                signal.Filter_M3_Stoch_Up = true;
            }

            signal.Filter_M3_Combined = signal.Filter_M3_Stoch_Up && signal.Filter_M3_MACD_Up;
        }

        private void CalculateStopLoss(M30BounceData bounce, EntrySignalData signal)
        {
            bool isLong = bounce.Direction == "LONG";

            double entryBarLow = Lows[0][0];
            double entryBarHigh = Highs[0][0];

            if (isLong)
            {
                signal.StopLossPrice = entryBarLow - (StopBufferPips * Instrument.MasterInstrument.TickSize);
                signal.StopDistance = signal.M1EntryPrice - signal.StopLossPrice;
            }
            else
            {
                signal.StopLossPrice = entryBarHigh + (StopBufferPips * Instrument.MasterInstrument.TickSize);
                signal.StopDistance = signal.StopLossPrice - signal.M1EntryPrice;
            }
        }

        private void UpdateEntrySignalOutcomes(M30BounceData bounce)
        {
            bool isLong = bounce.Direction == "LONG";

            foreach (var signal in bounce.EntrySignals)
            {
                if (signal.StopHit)
                    continue; // Already stopped out

                int barsInTrade = CurrentBars[0] - signal.M1EntryBar;

                double currentHigh = Highs[0][0];
                double currentLow = Lows[0][0];

                // Update MFE and MAE
                if (isLong)
                {
                    double currentMFE = currentHigh - signal.M1EntryPrice;
                    double currentMAE = signal.M1EntryPrice - currentLow;

                    if (currentMFE > signal.MFE_AfterEntry)
                        signal.MFE_AfterEntry = currentMFE;

                    if (currentMAE > signal.MAE_AfterEntry)
                        signal.MAE_AfterEntry = currentMAE;

                    // Check stop
                    if (currentLow <= signal.StopLossPrice)
                    {
                        signal.StopHit = true;
                        signal.BarsToStop = barsInTrade;
                    }

                    // Check targets
                    if (!signal.Reached10Pips && currentMFE >= 10 * Instrument.MasterInstrument.TickSize)
                    {
                        signal.Reached10Pips = true;
                        signal.BarsTo10Pips = barsInTrade;
                    }
                    if (!signal.Reached20Pips && currentMFE >= 20 * Instrument.MasterInstrument.TickSize)
                    {
                        signal.Reached20Pips = true;
                        signal.BarsTo20Pips = barsInTrade;
                    }
                    if (!signal.Reached30Pips && currentMFE >= 30 * Instrument.MasterInstrument.TickSize)
                    {
                        signal.Reached30Pips = true;
                        signal.BarsTo30Pips = barsInTrade;
                    }
                }
                else // SHORT
                {
                    double currentMFE = signal.M1EntryPrice - currentLow;
                    double currentMAE = currentHigh - signal.M1EntryPrice;

                    if (currentMFE > signal.MFE_AfterEntry)
                        signal.MFE_AfterEntry = currentMFE;

                    if (currentMAE > signal.MAE_AfterEntry)
                        signal.MAE_AfterEntry = currentMAE;

                    // Check stop
                    if (currentHigh >= signal.StopLossPrice)
                    {
                        signal.StopHit = true;
                        signal.BarsToStop = barsInTrade;
                    }

                    // Check targets
                    if (!signal.Reached10Pips && currentMFE >= 10 * Instrument.MasterInstrument.TickSize)
                    {
                        signal.Reached10Pips = true;
                        signal.BarsTo10Pips = barsInTrade;
                    }
                    if (!signal.Reached20Pips && currentMFE >= 20 * Instrument.MasterInstrument.TickSize)
                    {
                        signal.Reached20Pips = true;
                        signal.BarsTo20Pips = barsInTrade;
                    }
                    if (!signal.Reached30Pips && currentMFE >= 30 * Instrument.MasterInstrument.TickSize)
                    {
                        signal.Reached30Pips = true;
                        signal.BarsTo30Pips = barsInTrade;
                    }
                }
            }
        }

        #endregion

        #region CSV Logging

        private void WriteAllEntrySignals(string bounceID, M30BounceData bounce)
        {
            if (csvWriter == null || bounce.EntrySignals.Count == 0)
                return;

            foreach (var signal in bounce.EntrySignals)
            {
                csvWriter.WriteLine($"{bounce.BounceID},{bounce.Direction},{bounce.M30BounceTime:yyyy-MM-dd HH:mm:ss},{bounce.M30BouncePrice:F5}," +
                    $"{signal.SignalNumber},{signal.M1EntryTime:yyyy-MM-dd HH:mm:ss},{signal.M1EntryPrice:F5}," +
                    $"{signal.M1_StochK_Entry:F2},{signal.M1_StochD_Entry:F2},{signal.M1_StochK_Prev:F2},{signal.M1_StochD_Prev:F2}," +
                    $"{signal.InitialPeak:F5},{signal.RetracementDepth:F5},{signal.RetracementPct:F2}," +
                    $"{signal.Filter_D_Below20},{signal.Filter_M1_SMA50_Up},{signal.Filter_M3_Stoch_Up},{signal.Filter_M3_MACD_Up},{signal.Filter_M3_Combined}," +
                    $"{signal.M3_StochK:F2},{signal.M3_StochK_Prev:F2},{signal.M3_MACD:F4},{signal.M3_MACD_Prev:F4}," +
                    $"{signal.M1_SMA50_Current:F5},{signal.M1_SMA50_Prev:F5}," +
                    $"{signal.StopLossPrice:F5},{signal.StopDistance:F5}," +
                    $"{signal.StopHit},{signal.BarsToStop},{signal.MAE_AfterEntry:F5},{signal.MFE_AfterEntry:F5}," +
                    $"{signal.Reached10Pips},{signal.Reached20Pips},{signal.Reached30Pips}," +
                    $"{signal.BarsTo10Pips},{signal.BarsTo20Pips},{signal.BarsTo30Pips}");
            }

            csvWriter.Flush();

            Print($"✅ [{bounceID}] Logged {bounce.EntrySignals.Count} entry signals");
        }

        #endregion
    }
}

//--------------------------------------------------------------------------------------------------------------------------------------
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
using System.IO;
using System.Globalization;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class TrendContinuationWithMomentumStrategy : Strategy
    {
        #region Variables
        private string instrumentName = "";
        private string currentTimeFrame = "";
        
        // Entry state tracking for LONG trades
        private bool hasEMAHighTouched = false;
        private bool isInLongPullbackPhase = false;
        private bool longPullbackConfirmed = false; // NEW: Track pullback confirmation
        private int barsAboveEMAHigh = 0;
        private double longPullbackHighWaterMark = 0;
        private bool longInfoBoxCreated = false;
        private int longTriggerBarIndex = 0;
        private double longTriggerBarLow = 0;
        private double longTriggerBarHigh = 0;
        private int longClosesAgainstCount = 0;
        private int longHighestBarIndex = 0; // Bar index of highest price before pullback
        
        // NEW: Extreme price tracking (from TrendlineAfterPullbackWriter)
        private DateTime longExtremeTime = DateTime.MinValue;
        private double longExtremePrice = 0;
        private bool longTrackingExtreme = false;
        
        // NEW: Pullback start time tracking (like SetupState.PullbackStartTime)
        private DateTime longPullbackStartTime = DateTime.MinValue;
        
        // NEW: Delayed entry retry tracking
        private bool longRetryActive = false;
        private int longRetryStartBar = -1;
        private int longDelayedEntryBars = 0;
        private string longInitialInfoBoxId = ""; // Track initial blocked info box for deletion
        private bool longTriggerAttempted = false; // Track if any trigger attempt was made
        
        // Entry state tracking for SHORT trades  
        private bool hasEMALowTouched = false;
        private bool isInShortPullbackPhase = false;
        private bool shortPullbackConfirmed = false; // NEW: Track pullback confirmation
        private int barsBelowEMALow = 0;
        private double shortPullbackLowWaterMark = 0;
        private bool shortInfoBoxCreated = false;
        private int shortTriggerBarIndex = 0;
        private double shortTriggerBarLow = 0;
        private double shortTriggerBarHigh = 0;
        private int shortClosesAgainstCount = 0;
        private int shortLowestBarIndex = 0; // Bar index of lowest price before pullback
        
        // NEW: Extreme price tracking (from TrendlineAfterPullbackWriter)
        private DateTime shortExtremeTime = DateTime.MinValue;
        private double shortExtremePrice = 0;
        private bool shortTrackingExtreme = false;
        
        // NEW: Pullback start time tracking (like SetupState.PullbackStartTime)
        private DateTime shortPullbackStartTime = DateTime.MinValue;
        
        // NEW: Delayed entry retry tracking
        private bool shortRetryActive = false;
        private int shortRetryStartBar = -1;
        private int shortDelayedEntryBars = 0;
        private string shortInitialInfoBoxId = ""; // Track initial blocked info box for deletion
        private bool shortTriggerAttempted = false; // Track if any trigger attempt was made
        
        // ATR for buffer calculations
        private ATR atr;
        
        // Technical indicators for M1
        private EMA ema34High;
        private EMA ema34Low;
        private SMA sma5Typical;
        private SMA smaCycle; // SMA(SMACyclePeriod) for cycle tracking
        private Stochastics stochM1; // Main timeframe stochastic for cycle tracking
        private Series<double> typicalPrice;
        
        // Technical indicators for M3 (BarsArray[1])
        private MACD macdM3;
        private Stochastics stochM3;

        // Technical indicators for M9 (BarsArray[2])
        private MACD macdM9;
        private Stochastics stochM9;
        
        // EMA34SR indicators for higher timeframes
        private EMA emaHigh_M15, emaLow_M15;
        private EMA emaHigh_M30, emaLow_M30;
        private EMA emaHigh_M60, emaLow_M60;
        private EMA emaHigh_M240, emaLow_M240;
        private EMA emaHigh_Daily, emaLow_Daily;
        private EMA emaHigh_Weekly, emaLow_Weekly;

        // Multi-timeframe trend filtering indicators
        private SMA sma_M3, sma_M9, sma_M15, sma_M30, sma_M60, sma_M240, sma_Daily, sma_Weekly;
        private MACD macd_M3, macd_M9, macd_M15, macd_M30, macd_M60, macd_M240, macd_Daily, macd_Weekly;

        // Chart timeframe MACD move filtering
        private MACD chartMacd;

        // Trend state tracking variables
        private string priceTrend_M3 = "Unknown", priceTrend_M9 = "Unknown", priceTrend_M15 = "Unknown", priceTrend_M30 = "Unknown", priceTrend_M60 = "Unknown";
        private string priceTrend_M240 = "Unknown", priceTrend_Daily = "Unknown", priceTrend_Weekly = "Unknown";
        private string macdTrend_M3 = "Unknown", macdTrend_M9 = "Unknown", macdTrend_M15 = "Unknown", macdTrend_M30 = "Unknown", macdTrend_M60 = "Unknown";
        private string macdTrend_M240 = "Unknown", macdTrend_Daily = "Unknown", macdTrend_Weekly = "Unknown";
        private int priceTrendNumber_M3 = 0, priceTrendNumber_M9 = 0, priceTrendNumber_M15 = 0, priceTrendNumber_M30 = 0, priceTrendNumber_M60 = 0;
        private int priceTrendNumber_M240 = 0, priceTrendNumber_Daily = 0, priceTrendNumber_Weekly = 0;
        private int macdTrendNumber_M3 = 0, macdTrendNumber_M9 = 0, macdTrendNumber_M15 = 0, macdTrendNumber_M30 = 0, macdTrendNumber_M60 = 0;
        private int macdTrendNumber_M240 = 0, macdTrendNumber_Daily = 0, macdTrendNumber_Weekly = 0;

        // Advanced Cycle Tracking Variables (147 total variables)

        // Enums for Advanced Cycle Tracking
        public enum SMADirection { Unknown, Up, Down }
        public enum CycleState { WaitingForCross, BelowFifty, AboveFifty }
        public enum MomentumTrend { Unknown, Up, Down }

        // Data structure for MACD point tracking
        private class MACDPoint
        {
            public DateTime TimeStamp { get; set; }
            public bool IsHigh { get; set; }
            public double MACDValue { get; set; }
            public MomentumTrend Trend { get; set; }
            public int TrendNumber { get; set; }
            public bool IsProcessed { get; set; } = false;

            // Additional properties for advanced cycle tracking
            public DateTime Time { get; set; }
            public double Value { get; set; }
            public int BarIndex { get; set; }
        }

        // SMA Direction Tracking (7 timeframes × 6 variables = 42 variables)
        private SMADirection lastNonFlatSMADirection_M3 = SMADirection.Unknown, lastNonFlatSMADirection_M9 = SMADirection.Unknown;
        private SMADirection lastNonFlatSMADirection_M15 = SMADirection.Unknown, lastNonFlatSMADirection_M30 = SMADirection.Unknown;
        private SMADirection lastNonFlatSMADirection_M60 = SMADirection.Unknown, lastNonFlatSMADirection_M240 = SMADirection.Unknown;
        private SMADirection lastNonFlatSMADirection_Daily = SMADirection.Unknown, lastNonFlatSMADirection_Weekly = SMADirection.Unknown;

        private SMADirection pendingDirection_M3 = SMADirection.Unknown, pendingDirection_M9 = SMADirection.Unknown;
        private SMADirection pendingDirection_M15 = SMADirection.Unknown, pendingDirection_M30 = SMADirection.Unknown;
        private SMADirection pendingDirection_M60 = SMADirection.Unknown, pendingDirection_M240 = SMADirection.Unknown;
        private SMADirection pendingDirection_Daily = SMADirection.Unknown, pendingDirection_Weekly = SMADirection.Unknown;

        private int consecutiveBarsInPendingDirection_M3 = 0, consecutiveBarsInPendingDirection_M9 = 0;
        private int consecutiveBarsInPendingDirection_M15 = 0, consecutiveBarsInPendingDirection_M30 = 0;
        private int consecutiveBarsInPendingDirection_M60 = 0, consecutiveBarsInPendingDirection_M240 = 0;
        private int consecutiveBarsInPendingDirection_Daily = 0, consecutiveBarsInPendingDirection_Weekly = 0;

        private DateTime lastSMADirectionChangeTime_M3, lastSMADirectionChangeTime_M9, lastSMADirectionChangeTime_M15;
        private DateTime lastSMADirectionChangeTime_M30, lastSMADirectionChangeTime_M60, lastSMADirectionChangeTime_M240;
        private DateTime lastSMADirectionChangeTime_Daily, lastSMADirectionChangeTime_Weekly;

        private int smaDirectionChanges_M3 = 0, smaDirectionChanges_M9 = 0, smaDirectionChanges_M15 = 0;
        private int smaDirectionChanges_M30 = 0, smaDirectionChanges_M60 = 0, smaDirectionChanges_M240 = 0;
        private int smaDirectionChanges_Daily = 0, smaDirectionChanges_Weekly = 0;

        // Stochastic Cycle Tracking (7 timeframes × 8 variables = 56 variables)
        private CycleState currentState_M3 = CycleState.WaitingForCross, currentState_M9 = CycleState.WaitingForCross;
        private CycleState currentState_M15 = CycleState.WaitingForCross, currentState_M30 = CycleState.WaitingForCross;
        private CycleState currentState_M60 = CycleState.WaitingForCross, currentState_M240 = CycleState.WaitingForCross;
        private CycleState currentState_Daily = CycleState.WaitingForCross, currentState_Weekly = CycleState.WaitingForCross;

        private bool lookingForLow_M3 = false, lookingForLow_M9 = false, lookingForLow_M15 = false;
        private bool lookingForLow_M30 = false, lookingForLow_M60 = false, lookingForLow_M240 = false;
        private bool lookingForLow_Daily = false, lookingForLow_Weekly = false;

        private double extremeValue_M3 = 0, extremeValue_M9 = 0, extremeValue_M15 = 0;
        private double extremeValue_M30 = 0, extremeValue_M60 = 0, extremeValue_M240 = 0;
        private double extremeValue_Daily = 0, extremeValue_Weekly = 0;

        private DateTime extremeTime_M3, extremeTime_M9, extremeTime_M15, extremeTime_M30;
        private DateTime extremeTime_M60, extremeTime_M240, extremeTime_Daily, extremeTime_Weekly;

        private DateTime cycleStartTime_M3, cycleStartTime_M9, cycleStartTime_M15, cycleStartTime_M30;
        private DateTime cycleStartTime_M60, cycleStartTime_M240, cycleStartTime_Daily, cycleStartTime_Weekly;

        private double currentCycleExtremeClose_M3 = 0, currentCycleExtremeClose_M9 = 0, currentCycleExtremeClose_M15 = 0;
        private double currentCycleExtremeClose_M30 = 0, currentCycleExtremeClose_M60 = 0, currentCycleExtremeClose_M240 = 0;
        private double currentCycleExtremeClose_Daily = 0, currentCycleExtremeClose_Weekly = 0;

        private double previousCycleExtremeClose_M3 = 0, previousCycleExtremeClose_M9 = 0, previousCycleExtremeClose_M15 = 0;
        private double previousCycleExtremeClose_M30 = 0, previousCycleExtremeClose_M60 = 0, previousCycleExtremeClose_M240 = 0;
        private double previousCycleExtremeClose_Daily = 0, previousCycleExtremeClose_Weekly = 0;

        private bool hasPreviousCyclePrice_M3 = false, hasPreviousCyclePrice_M9 = false, hasPreviousCyclePrice_M15 = false;
        private bool hasPreviousCyclePrice_M30 = false, hasPreviousCyclePrice_M60 = false, hasPreviousCyclePrice_M240 = false;
        private bool hasPreviousCyclePrice_Daily = false, hasPreviousCyclePrice_Weekly = false;

        // Price Trend Cycle Counting (7 timeframes × 2 variables = 14 variables)
        private int stochCyclesSinceLastSMAChange_M3 = 0, stochCyclesSinceLastSMAChange_M9 = 0, stochCyclesSinceLastSMAChange_M15 = 0;
        private int stochCyclesSinceLastSMAChange_M30 = 0, stochCyclesSinceLastSMAChange_M60 = 0, stochCyclesSinceLastSMAChange_M240 = 0;
        private int stochCyclesSinceLastSMAChange_Daily = 0, stochCyclesSinceLastSMAChange_Weekly = 0;
        // Note: priceTrendNumber_XX variables are reused and now represent cycle counts

        // MACD Trend Move Tracking (7 timeframes × 5 variables = 35 variables)
        private MomentumTrend currentMACDTrend_M3 = MomentumTrend.Unknown, currentMACDTrend_M9 = MomentumTrend.Unknown;
        private MomentumTrend currentMACDTrend_M15 = MomentumTrend.Unknown, currentMACDTrend_M30 = MomentumTrend.Unknown;
        private MomentumTrend currentMACDTrend_M60 = MomentumTrend.Unknown, currentMACDTrend_M240 = MomentumTrend.Unknown;
        private MomentumTrend currentMACDTrend_Daily = MomentumTrend.Unknown, currentMACDTrend_Weekly = MomentumTrend.Unknown;

        // Note: macdTrendNumber_XX variables are reused and now represent move counts

        private double lastMACDExtreme_M3 = 0, lastMACDExtreme_M9 = 0, lastMACDExtreme_M15 = 0;
        private double lastMACDExtreme_M30 = 0, lastMACDExtreme_M60 = 0, lastMACDExtreme_M240 = 0;
        private double lastMACDExtreme_Daily = 0, lastMACDExtreme_Weekly = 0;

        private List<MACDPoint> macdPoints_M3, macdPoints_M9, macdPoints_M15, macdPoints_M30;
        private List<MACDPoint> macdPoints_M60, macdPoints_M240, macdPoints_Daily, macdPoints_Weekly;

        // Chart timeframe MACD points list
        private List<MACDPoint> macdPointsChart;

        // Advanced Cycle Tracking State Objects
        private SMADirectionState smaDirectionState_M3, smaDirectionState_M9, smaDirectionState_M15, smaDirectionState_M30;
        private SMADirectionState smaDirectionState_M60, smaDirectionState_M240, smaDirectionState_Daily, smaDirectionState_Weekly;

        private StochasticCycleState stochCycleState_M3, stochCycleState_M9, stochCycleState_M15, stochCycleState_M30;
        private StochasticCycleState stochCycleState_M60, stochCycleState_M240, stochCycleState_Daily, stochCycleState_Weekly;

        private MACDTrendState macdTrendState_M3, macdTrendState_M9, macdTrendState_M15, macdTrendState_M30;
        private MACDTrendState macdTrendState_M60, macdTrendState_M240, macdTrendState_Daily, macdTrendState_Weekly;

        // Chart timeframe MACD move tracking state
        private MACDTrendState chartMACDTrendState;
        private SMADirectionState chartSMADirectionState;
        private StochasticCycleState chartStochCycleState;

        // New indicators for cycle tracking
        private ATR atr14_Chart, atr14_M3, atr14_M9, atr14_M15, atr14_M30, atr14_M60, atr14_M240, atr14_Daily, atr14_Weekly;
        private Stochastics stoch_M15, stoch_M30, stoch_M60, stoch_M240, stoch_Daily, stoch_Weekly;
        // Note: stochM3 and stochM9 already exist - reuse them!

        // SMA Cycle tracking variables (main timeframe)
        private string currentSMADirection = "Unknown"; // Current SMA(50) direction (Up/Down/Unknown)
        private string previousStochDirection = "Unknown"; // Previous stochastic direction
        private int smaCycleCount = 0; // Number of stochastic cycles in current SMA direction

        // Bounce count tracking variables
        private int bouncesInCurrentSMADirection = 0; // Number of bounces since SMA direction change (only counts bounces aligned with SMA)
        private string lastTrackedSMADirection = "Unknown"; // Last SMA direction for detecting changes
        private int lastBounceCountedBar = -1; // Track which bar we last counted a bounce on
        private double lastCountedExtremeHigh = 0; // Extreme high when we last counted a long bounce
        private double lastCountedExtremeLow = 0; // Extreme low when we last counted a short bounce
        private bool closedAboveLastExtremeHigh = false; // Flag: has price closed above last counted extreme high
        private bool closedBelowLastExtremeLow = false; // Flag: has price closed below last counted extreme low

        // Sophisticated SMA direction tracking (MomentumAgeIndicator-style) - uses existing SMADirection enum from line 138
        private SMADirection lastNonFlatSMADirection = SMADirection.Unknown;
        private SMADirection pendingDirection = SMADirection.Unknown;
        private int consecutiveBarsInPendingDirection = 0;

        // Chart timeframe MACD tracking variables
        private string chartMACDTrend = "Unknown"; // Chart timeframe MACD trend direction
        private int chartMACDMoveCount = 0; // Chart timeframe MACD move count
        
        // Custom phantom bar arrays for M3 (20 bars deep) - using regular arrays to avoid NT8 interference
        private double[] phantomM3Opens = new double[20];
        private double[] phantomM3Highs = new double[20];
        private double[] phantomM3Lows = new double[20];
        private double[] phantomM3Closes = new double[20];
        private DateTime[] phantomM3Times = new DateTime[20];
        
        // Custom phantom bar arrays for M9 (20 bars deep) - using regular arrays to avoid NT8 interference
        private double[] phantomM9Opens = new double[20];
        private double[] phantomM9Highs = new double[20];
        private double[] phantomM9Lows = new double[20];
        private double[] phantomM9Closes = new double[20];
        private DateTime[] phantomM9Times = new DateTime[20];
        
        // Track M3 and M9 bar changes
        private DateTime lastSeenM3Time = DateTime.MinValue;
        private DateTime lastSeenM9Time = DateTime.MinValue;
        
        // Stochastic parameters (matching NT8 settings)
        private int stochK = 5;
        private int stochD = 3;
        private int stochSmooth = 2;
        
        // Manual stochastic calculation arrays for M3
        private double[] fastKValuesM3 = new double[50]; // Ring buffer for raw %K values
        private int fastKIndexM3 = 0;
        private int fastKCountM3 = 0;
        
        private double[] smoothedKValuesM3 = new double[50]; // Ring buffer for smoothed %K values (for %D calculation)
        private int smoothedKIndexM3 = 0;
        private int smoothedKCountM3 = 0;
        
        // Manual stochastic calculation arrays for M9
        private double[] fastKValuesM9 = new double[50]; // Ring buffer for raw %K values
        private int fastKIndexM9 = 0;
        private int fastKCountM9 = 0;
        
        private double[] smoothedKValuesM9 = new double[50]; // Ring buffer for smoothed %K values (for %D calculation)
        private int smoothedKIndexM9 = 0;
        private int smoothedKCountM9 = 0;
        
        // MACD parameters (matching your NT8 indicator settings)
        private int macdFast = 5;
        private int macdSlow = 20;
        private int macdSmooth = 30;
        
        // Manual MACD calculation arrays for M3 (only updated on M3 closes)
        private double[] fastEmaValuesM3 = new double[200]; // EMA ring buffer
        private double[] slowEmaValuesM3 = new double[200]; // EMA ring buffer
        private double[] macdLineValuesM3 = new double[200]; // MACD Line ring buffer
        private double[] signalLineValuesM3 = new double[200]; // Signal Line ring buffer
        private int macdIndexM3 = 0;
        private int macdCountM3 = 0;
        
        // Manual MACD calculation arrays for M9 (only updated on M9 closes)
        private double[] fastEmaValuesM9 = new double[200]; // EMA ring buffer
        private double[] slowEmaValuesM9 = new double[200]; // EMA ring buffer
        private double[] macdLineValuesM9 = new double[200]; // MACD Line ring buffer
        private double[] signalLineValuesM9 = new double[200]; // Signal Line ring buffer
        private int macdIndexM9 = 0;
        private int macdCountM9 = 0;
        
        // EMA constants (exact NT8 formula from @MACD.cs)
        private double constant1; // Fast EMA multiplier: 2.0 / (1 + Fast)
        private double constant2; // Fast EMA complement: 1 - 2.0 / (1 + Fast)
        private double constant3; // Slow EMA multiplier: 2.0 / (1 + Slow)
        private double constant4; // Slow EMA complement: 1 - 2.0 / (1 + Slow)
        private double constant5; // Signal EMA multiplier: 2.0 / (1 + Smooth)
        private double constant6; // Signal EMA complement: 1 - 2.0 / (1 + Smooth)
        
        // Current phantom values (updated every M1 close)
        private double phantomM3StochK = 50.0;
        private double phantomM3StochD = 50.0;
        private double phantomM3MacdLine = 0.0;
        private double phantomM3SignalLine = 0.0;
        
        private double phantomM9StochK = 50.0;
        private double phantomM9StochD = 50.0;
        private double phantomM9MacdLine = 0.0;
        private double phantomM9SignalLine = 0.0;
        
        // MACD and Stochastic indicators are handled by built-in NT8 indicators (macdM3, stochM3, macdM9, stochM9)


        // Position sizing and risk management variables
        private double calculatedPositionSize = 0;
        private double stopLossPrice = 0;
        private double riskAmount = 0;
        private double targetPrice = 0;
        
        // Bounce validation results storage (to eliminate duplicate calls)
        private bool currentBounceM3Pass = false;
        private bool currentBounceM9Pass = false;
        private bool currentBounceSRPass = true;  // default true if not enabled
        private bool currentBounceBufferPass = true; // default true if not enabled
        private bool currentBounceSMACyclePass = true; // default true if not enabled
        private bool currentBounceBounceCountPass = true; // default true if not enabled
        private bool currentBounceSMADirectionPass = true; // default true if not enabled
        private bool currentBounceMACDDirectionPass = true; // default true if not enabled
        private bool currentBounceMACDMovePass = true; // default true if not enabled
        private bool currentBounceTrendFilterPass = true; // default true if not enabled
        private string currentBounceRejectionReason = "";
        private double currentBounceEntry = 0;
        private double currentBounceStopLoss = 0;
        private double currentBounceTarget = 0;
        private double currentBounceSize = 0;
        private bool currentBounceDataReady = false;
        private bool currentBounceIsLong = true;
        private double riskRewardRatio = 0;
        
        
        
        // DEBUG: Track previous values to detect intrabar updates
        private double prevM3MACD = double.MinValue;
        private double prevM3StochK = double.MinValue;
        private double prevM9MACD = double.MinValue;
        private double prevM9StochK = double.MinValue;
        
        // Debug and display
        private bool showDebugInfo = false;
        
        // Information box tracking
        private int bounceCounter = 0;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Trend continuation signals with multi-timeframe momentum confirmation";
                Name = "TrendContinuationWithMomentumStrategy";
                Calculate = Calculate.OnBarClose;
                // IsOverlay and DrawOnPricePanel removed - not needed for strategies
                DisplayInDataBox = true;
                ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;

                // Strategy position settings - only 1 trade at a time
                EntriesPerDirection = 1;  // Max 1 long OR 1 short position at a time
                EntryHandling = EntryHandling.AllEntries;  // Allow reversals

                // Historical data settings
                BarsRequiredToTrade = 50;  // Minimum bars before strategy starts (M3 and M9 need 50)
                MaximumBarsLookBack = MaximumBarsLookBack.Infinite;  // Process all available historical bars

                // M3 MACD visual plots removed

                
                // Default settings
                MinBarsAboveEMA = 3;
                EnableM3Filter = true;  // Default enabled for backward compatibility
                EnableM9Filter = true;  // Default enabled for backward compatibility
                StartTradingTime = 0;  // 0 = disabled (trade all day)
                EndTradingTime = 0;    // 0 = disabled (trade all day)
                EnableMonday = true;    // Default all weekdays enabled
                EnableTuesday = true;
                EnableWednesday = true;
                EnableThursday = true;
                EnableFriday = true;
                EnableSaturday = false;  // Default weekend disabled
                EnableSunday = false;    // Default weekend disabled
                ShowDebugInfo = false;
                ShowStochasticCycleDebug = false;
                ShowSignals = true;
                ShowInfoBoxes = true;
                ShowMACDCounts = false;
                ShowM3MACDCounts = false;
                ShowBounceCounter = false;
                ShowM3PriceTrendCounts = false;
                ShowTradeDetails = false;
                ShowBounceCriteria = false;
                MaxClosesAgainstTrend = 3;
                ATRBufferPercent = 3000.0;
                MinBarsToTrigger = 4;
                RetryBars = 1; // Default retry for 1 bar after initial trigger block
                
				IsDataSeriesRequired = true;  // Enable phantom bars
				
                // Risk Management defaults
                StopLossBufferPercent = 50.0;
                AccountRiskPercent = 1.0;
                RiskRewardRatio = 2.0;
                AccountSize = 100000;
                UseRealAccountSize = false;
                ShowPositionSizing = false;
                
                // EMA34SR Integration defaults
                EnableEMA34SR_M15 = false;
                EnableEMABuffers_M15 = false;
                EnableEMA34SR_M30 = false;
                EnableEMABuffers_M30 = false;
                EnableEMA34SR_M60 = false;
                EnableEMABuffers_M60 = false;
                EnableEMA34SR_M240 = false;
                EnableEMABuffers_M240 = false;
                EnableEMA34SR_Daily = false;
                EnableEMABuffers_Daily = false;
                EnableEMA34SR_Weekly = false;
                EnableEMABuffers_Weekly = false;
                EMABufferPercent = 30.0;
                MinimumRiskReward = 0.75;
                DistanceFromSRAsATRPercent = 50.0;
                MaxSRDistancePoints = 50.0;
                
                // SMA Cycle Filtering defaults
                EnableSMACycleFiltering = false;
                MaxSMACycles = 10;
                SMACyclePeriod = 50;
                EnableBounceCountFilter = false;
                MaxBouncesInSMADirection = 50; // High default - no behavior change
                EnableSMADirectionFilter = false;
                EnableMACDDirectionFilter = false;
                MACDReversalThreshold = 0.1; // 10% default

                // Chart MACD Move Filtering defaults
                EnableMACDMoveFiltering = false;
                MaxMACDMoves = 10;
                
                // Multi-Timeframe Trend Filtering defaults
                EnablePriceTrendFilter_M15 = false;
                EnableMomentumTrendFilter_M15 = false;
                EnablePriceTrendFilter_M30 = false;
                EnableMomentumTrendFilter_M30 = false;
                EnablePriceTrendFilter_M60 = false;
                EnableMomentumTrendFilter_M60 = false;
                EnablePriceTrendFilter_M240 = false;
                EnableMomentumTrendFilter_M240 = false;
                EnablePriceTrendFilter_Daily = false;
                EnableMomentumTrendFilter_Daily = false;
                EnablePriceTrendFilter_Weekly = false;
                EnableMomentumTrendFilter_Weekly = false;
                EnablePriceTrendFilter_M3 = false;
                EnableMomentumTrendFilter_M3 = false;
                EnablePriceTrendFilter_M9 = false;
                EnableMomentumTrendFilter_M9 = false;

                // Advanced Cycle Tracking defaults - explicit initialization
                SMAFlatThreshold = 0.01;
                SMADirectionConsecutiveBars = 3;
                CycleProgressionBufferPercent = 50.0;
                EnableCycleProgressionValidation = true;
            }
            else if (State == State.Configure)
            {
                try
                {
                    // Add secondary data series
                    // Note: MaximumBarsLookBack.Infinite (set above) ensures all data series load sufficient history
                    AddDataSeries(Data.BarsPeriodType.Minute, 3);  // M3 - BarsArray[1]
                    AddDataSeries(Data.BarsPeriodType.Minute, 9);  // M9 - BarsArray[2]

                    // Add EMA34SR and Trend Filtering timeframe data series (only if enabled)
                    if (EnableEMA34SR_M15 || EnableEMABuffers_M15 || EnablePriceTrendFilter_M15 || EnableMomentumTrendFilter_M15)
                        AddDataSeries(Data.BarsPeriodType.Minute, 15);  // M15 - BarsArray[3]
                    if (EnableEMA34SR_M30 || EnableEMABuffers_M30 || EnablePriceTrendFilter_M30 || EnableMomentumTrendFilter_M30)
                        AddDataSeries(Data.BarsPeriodType.Minute, 30);  // M30 - BarsArray[4]
                    if (EnableEMA34SR_M60 || EnableEMABuffers_M60 || EnablePriceTrendFilter_M60 || EnableMomentumTrendFilter_M60)
                        AddDataSeries(Data.BarsPeriodType.Minute, 60);  // M60 - BarsArray[5]
                    if (EnableEMA34SR_M240 || EnableEMABuffers_M240 || EnablePriceTrendFilter_M240 || EnableMomentumTrendFilter_M240)
                        AddDataSeries(Data.BarsPeriodType.Minute, 240); // M240 - BarsArray[6]
                    if (EnableEMA34SR_Daily || EnableEMABuffers_Daily || EnablePriceTrendFilter_Daily || EnableMomentumTrendFilter_Daily)
                        AddDataSeries(Data.BarsPeriodType.Day, 1);      // Daily - BarsArray[7]
                    if (EnableEMA34SR_Weekly || EnableEMABuffers_Weekly || EnablePriceTrendFilter_Weekly || EnableMomentumTrendFilter_Weekly)
                        AddDataSeries(Data.BarsPeriodType.Week, 1);     // Weekly - BarsArray[8]
                }
                catch (Exception ex)
                {
                    Print($"❌ ERROR in State.Configure: {ex.Message}");
                    Print($"   Stack: {ex.StackTrace}");
                }
            }
            else if (State == State.DataLoaded)
            {
                try
                {
                    instrumentName = Instrument.MasterInstrument.Name;
                    currentTimeFrame = GetTimeFrameString(BarsPeriod);

                    // Initialize M1 indicators
                    ema34High = EMA(High, 34);
                    ema34Low = EMA(Low, 34);
                typicalPrice = new Series<double>(this);
                sma5Typical = SMA(typicalPrice, 5);
                smaCycle = SMA(Close, SMACyclePeriod); // SMA for cycle tracking
                stochM1 = Stochastics(3, 5, 2); // Main timeframe stochastic for cycle tracking (correct parameters)
                atr = ATR(14);

                // Initialize chart timeframe MACD for move filtering
                chartMacd = MACD(5, 20, 30);
                
                // Initialize M3 indicators (BarsArray[1]) - ACTUAL M3 timeframe data
                // MACD(fast=5, slow=20, signal=30) - as originally specified
                macdM3 = MACD(BarsArray[1], 5, 20, 30);
                // Stochastics(periodD=3, periodK=5, smooth=2) - as originally specified
                stochM3 = Stochastics(BarsArray[1], 3, 5, 2);

                // Initialize M9 indicators (BarsArray[2]) - ACTUAL M9 timeframe data
                // MACD(fast=5, slow=20, signal=30) - as originally specified
                macdM9 = MACD(BarsArray[2], 5, 20, 30);
                // Stochastics(periodD=3, periodK=5, smooth=2) - as originally specified
                stochM9 = Stochastics(BarsArray[2], 3, 5, 2);
                
                // Initialize EMA34SR and Trend Filtering indicators for enabled timeframes
                int barsArrayIndex = 3; // Start after M3(1) and M9(2)

                // Initialize M3 and M9 trend indicators (they use fixed BarsArray indices)
                if (EnablePriceTrendFilter_M3 || EnableMomentumTrendFilter_M3)
                {
                    if (EnablePriceTrendFilter_M3)
                        sma_M3 = SMA(BarsArray[1], SMACyclePeriod); // M3 is BarsArray[1]
                    if (EnableMomentumTrendFilter_M3)
                        macd_M3 = MACD(BarsArray[1], 5, 20, 30);
                }

                if (EnablePriceTrendFilter_M9 || EnableMomentumTrendFilter_M9)
                {
                    if (EnablePriceTrendFilter_M9)
                        sma_M9 = SMA(BarsArray[2], SMACyclePeriod); // M9 is BarsArray[2]
                    if (EnableMomentumTrendFilter_M9)
                        macd_M9 = MACD(BarsArray[2], 5, 20, 30);
                }

                // Initialize ATR indicators for all timeframes (needed for cycle progression validation)
                atr14_Chart = ATR(BarsArray[0], 14); // Chart timeframe
                atr14_M3 = ATR(BarsArray[1], 14); // M3
                atr14_M9 = ATR(BarsArray[2], 14); // M9

                if (EnableEMA34SR_M15 || EnableEMABuffers_M15 || EnablePriceTrendFilter_M15 || EnableMomentumTrendFilter_M15)
                {
                    if (EnableEMA34SR_M15 || EnableEMABuffers_M15)
                    {
                        emaHigh_M15 = EMA(Highs[barsArrayIndex], 34);
                        emaLow_M15 = EMA(Lows[barsArrayIndex], 34);
                    }
                    if (EnablePriceTrendFilter_M15)
                        sma_M15 = SMA(BarsArray[barsArrayIndex], SMACyclePeriod);
                    if (EnableMomentumTrendFilter_M15)
                        macd_M15 = MACD(BarsArray[barsArrayIndex], 5, 20, 30);
                    // Add stochastic and ATR for M15 cycle tracking
                    stoch_M15 = Stochastics(BarsArray[barsArrayIndex], 3, 5, 2);
                    atr14_M15 = ATR(BarsArray[barsArrayIndex], 14);
                    barsArrayIndex++;
                }
                if (EnableEMA34SR_M30 || EnableEMABuffers_M30 || EnablePriceTrendFilter_M30 || EnableMomentumTrendFilter_M30)
                {
                    if (EnableEMA34SR_M30 || EnableEMABuffers_M30)
                    {
                        emaHigh_M30 = EMA(Highs[barsArrayIndex], 34);
                        emaLow_M30 = EMA(Lows[barsArrayIndex], 34);
                    }
                    if (EnablePriceTrendFilter_M30)
                        sma_M30 = SMA(BarsArray[barsArrayIndex], SMACyclePeriod);
                    if (EnableMomentumTrendFilter_M30)
                        macd_M30 = MACD(BarsArray[barsArrayIndex], 5, 20, 30);
                    // Add stochastic and ATR for M30 cycle tracking
                    stoch_M30 = Stochastics(BarsArray[barsArrayIndex], 3, 5, 2);
                    atr14_M30 = ATR(BarsArray[barsArrayIndex], 14);
                    barsArrayIndex++;
                }
                if (EnableEMA34SR_M60 || EnableEMABuffers_M60 || EnablePriceTrendFilter_M60 || EnableMomentumTrendFilter_M60)
                {
                    if (EnableEMA34SR_M60 || EnableEMABuffers_M60)
                    {
                        emaHigh_M60 = EMA(Highs[barsArrayIndex], 34);
                        emaLow_M60 = EMA(Lows[barsArrayIndex], 34);
                    }
                    if (EnablePriceTrendFilter_M60)
                        sma_M60 = SMA(BarsArray[barsArrayIndex], SMACyclePeriod);
                    if (EnableMomentumTrendFilter_M60)
                        macd_M60 = MACD(BarsArray[barsArrayIndex], 5, 20, 30);
                    // Add stochastic and ATR for M60 cycle tracking
                    stoch_M60 = Stochastics(BarsArray[barsArrayIndex], 3, 5, 2);
                    atr14_M60 = ATR(BarsArray[barsArrayIndex], 14);
                    barsArrayIndex++;
                }
                if (EnableEMA34SR_M240 || EnableEMABuffers_M240 || EnablePriceTrendFilter_M240 || EnableMomentumTrendFilter_M240)
                {
                    if (EnableEMA34SR_M240 || EnableEMABuffers_M240)
                    {
                        emaHigh_M240 = EMA(Highs[barsArrayIndex], 34);
                        emaLow_M240 = EMA(Lows[barsArrayIndex], 34);
                    }
                    if (EnablePriceTrendFilter_M240)
                        sma_M240 = SMA(BarsArray[barsArrayIndex], SMACyclePeriod);
                    if (EnableMomentumTrendFilter_M240)
                        macd_M240 = MACD(BarsArray[barsArrayIndex], 5, 20, 30);
                    // Add stochastic and ATR for M240 cycle tracking
                    stoch_M240 = Stochastics(BarsArray[barsArrayIndex], 3, 5, 2);
                    atr14_M240 = ATR(BarsArray[barsArrayIndex], 14);
                    barsArrayIndex++;
                }
                if (EnableEMA34SR_Daily || EnableEMABuffers_Daily || EnablePriceTrendFilter_Daily || EnableMomentumTrendFilter_Daily)
                {
                    if (EnableEMA34SR_Daily || EnableEMABuffers_Daily)
                    {
                        emaHigh_Daily = EMA(Highs[barsArrayIndex], 34);
                        emaLow_Daily = EMA(Lows[barsArrayIndex], 34);
                    }
                    if (EnablePriceTrendFilter_Daily)
                        sma_Daily = SMA(BarsArray[barsArrayIndex], SMACyclePeriod);
                    if (EnableMomentumTrendFilter_Daily)
                        macd_Daily = MACD(BarsArray[barsArrayIndex], 5, 20, 30);
                    // Add stochastic and ATR for Daily cycle tracking
                    stoch_Daily = Stochastics(BarsArray[barsArrayIndex], 3, 5, 2);
                    atr14_Daily = ATR(BarsArray[barsArrayIndex], 14);
                    barsArrayIndex++;
                }
                if (EnableEMA34SR_Weekly || EnableEMABuffers_Weekly || EnablePriceTrendFilter_Weekly || EnableMomentumTrendFilter_Weekly)
                {
                    if (EnableEMA34SR_Weekly || EnableEMABuffers_Weekly)
                    {
                        emaHigh_Weekly = EMA(Highs[barsArrayIndex], 34);
                        emaLow_Weekly = EMA(Lows[barsArrayIndex], 34);
                    }
                    if (EnablePriceTrendFilter_Weekly)
                        sma_Weekly = SMA(BarsArray[barsArrayIndex], SMACyclePeriod);
                    if (EnableMomentumTrendFilter_Weekly)
                        macd_Weekly = MACD(BarsArray[barsArrayIndex], 5, 20, 30);
                    // Add stochastic and ATR for Weekly cycle tracking
                    stoch_Weekly = Stochastics(BarsArray[barsArrayIndex], 3, 5, 2);
                    atr14_Weekly = ATR(BarsArray[barsArrayIndex], 14);
                    barsArrayIndex++;
                }
                
                // Initialize M3 phantom bar arrays with default values
                for (int i = 0; i < 20; i++)
                {
                    phantomM3Opens[i] = 0;
                    phantomM3Highs[i] = 0;
                    phantomM3Lows[i] = double.MaxValue;
                    phantomM3Closes[i] = 0;
                    phantomM3Times[i] = DateTime.MinValue;
                }
                
                // Initialize M9 phantom bar arrays with default values
                for (int i = 0; i < 20; i++)
                {
                    phantomM9Opens[i] = 0;
                    phantomM9Highs[i] = 0;
                    phantomM9Lows[i] = double.MaxValue;
                    phantomM9Closes[i] = 0;
                    phantomM9Times[i] = DateTime.MinValue;
                }
                
                // Initialize M3 stochastic arrays
                for (int i = 0; i < 50; i++)
                {
                    fastKValuesM3[i] = 50.0; // Default %K value
                    smoothedKValuesM3[i] = 50.0; // Default smoothed %K value
                }
                
                // Initialize M9 stochastic arrays
                for (int i = 0; i < 50; i++)
                {
                    fastKValuesM9[i] = 50.0; // Default %K value
                    smoothedKValuesM9[i] = 50.0; // Default smoothed %K value
                }
                
                // Initialize M3 MACD arrays
                for (int i = 0; i < 200; i++)
                {
                    fastEmaValuesM3[i] = 0;
                    slowEmaValuesM3[i] = 0;
                    macdLineValuesM3[i] = 0;
                    signalLineValuesM3[i] = 0;
                }
                
                // Initialize M9 MACD arrays
                for (int i = 0; i < 200; i++)
                {
                    fastEmaValuesM9[i] = 0;
                    slowEmaValuesM9[i] = 0;
                    macdLineValuesM9[i] = 0;
                    signalLineValuesM9[i] = 0;
                }
                
                // Calculate EMA constants (exact NT8 formula from @MACD.cs)
                constant1 = 2.0 / (1 + macdFast);      // Fast multiplier: 2/(1+5) = 0.3333
                constant2 = 1 - 2.0 / (1 + macdFast);  // Fast complement: 1 - 0.3333 = 0.6667
                constant3 = 2.0 / (1 + macdSlow);      // Slow multiplier: 2/(1+20) = 0.0952  
                constant4 = 1 - 2.0 / (1 + macdSlow);  // Slow complement: 1 - 0.0952 = 0.9048
                constant5 = 2.0 / (1 + macdSmooth);    // Signal multiplier: 2/(1+30) = 0.0645
                constant6 = 1 - 2.0 / (1 + macdSmooth); // Signal complement: 1 - 0.0645 = 0.9355

                // Initialize MACD point lists for all timeframes
                macdPoints_M3 = new List<MACDPoint>();
                macdPoints_M9 = new List<MACDPoint>();
                macdPoints_M15 = new List<MACDPoint>();
                macdPoints_M30 = new List<MACDPoint>();
                macdPoints_M60 = new List<MACDPoint>();
                macdPoints_M240 = new List<MACDPoint>();
                macdPoints_Daily = new List<MACDPoint>();
                macdPoints_Weekly = new List<MACDPoint>();

                // Initialize chart timeframe MACD points list
                macdPointsChart = new List<MACDPoint>();

                // Note: Default values now set in property declarations

                // Initialize Advanced Cycle Tracking State Objects
                smaDirectionState_M3 = new SMADirectionState();
                smaDirectionState_M9 = new SMADirectionState();
                smaDirectionState_M15 = new SMADirectionState();
                smaDirectionState_M30 = new SMADirectionState();
                smaDirectionState_M60 = new SMADirectionState();
                smaDirectionState_M240 = new SMADirectionState();
                smaDirectionState_Daily = new SMADirectionState();
                smaDirectionState_Weekly = new SMADirectionState();

                stochCycleState_M3 = new StochasticCycleState();
                stochCycleState_M9 = new StochasticCycleState();
                stochCycleState_M15 = new StochasticCycleState();
                stochCycleState_M30 = new StochasticCycleState();
                stochCycleState_M60 = new StochasticCycleState();
                stochCycleState_M240 = new StochasticCycleState();
                stochCycleState_Daily = new StochasticCycleState();
                stochCycleState_Weekly = new StochasticCycleState();

                macdTrendState_M3 = new MACDTrendState();
                macdTrendState_M9 = new MACDTrendState();
                macdTrendState_M15 = new MACDTrendState();
                macdTrendState_M30 = new MACDTrendState();
                macdTrendState_M60 = new MACDTrendState();
                macdTrendState_M240 = new MACDTrendState();
                macdTrendState_Daily = new MACDTrendState();
                macdTrendState_Weekly = new MACDTrendState();

                // Initialize chart timeframe MACD trend state
                chartMACDTrendState = new MACDTrendState();
                chartSMADirectionState = new SMADirectionState();
                chartStochCycleState = new StochasticCycleState();

                if (ShowDebugInfo)
                {
                    Print($"✅ Strategy initialized for {instrumentName} {currentTimeFrame}");
                }
                }
                catch (Exception ex)
                {
                    Print($"❌ ERROR in State.DataLoaded: {ex.Message}");
                    Print($"   Stack: {ex.StackTrace}");
                }
            }
        }

        protected override void OnBarUpdate()
        {
            // UNCONDITIONAL DEBUG: Verify OnBarUpdate is being called
            if (CurrentBar % 100 == 0)
            {
                Print($"✅ OnBarUpdate called at Bar {CurrentBar}, Time {Time[0]:HH:mm:ss}, BarsInProgress={BarsInProgress}");
            }

            // CRITICAL: Check BarsInProgress FIRST before accessing any data
            if (BarsInProgress != 0)
                return; // Only process primary data series (M1)

            // UNCONDITIONAL DEBUG: Verify we passed BarsInProgress check
            if (CurrentBar % 100 == 0)
            {
                Print($"✅ Passed BarsInProgress check - CurrentBars: M1={CurrentBars[0]}, M3={CurrentBars[1]}, M9={CurrentBars[2]}");
            }

            // Skip if not enough data
            if (CurrentBars[0] < 34 || CurrentBars[1] < 50 || CurrentBars[2] < 50)
            {
                if (CurrentBar % 100 == 0)
                {
                    Print($"⏸️ Not enough bars - M1:{CurrentBars[0]}/34, M3:{CurrentBars[1]}/50, M9:{CurrentBars[2]}/50");
                }
                return;
            }

            // UNCONDITIONAL DEBUG: Verify we have enough bars
            if (CurrentBar % 100 == 0)
            {
                Print($"✅ Have enough bars - Starting strategy logic");
            }

            // M3 MACD visual plotting removed

            try
            {
                // First check if we're getting any bar updates at all
                if (ShowDebugInfo)
                {
                    Print($"🔵 {Time[0]:HH:mm:ss} OnBarUpdate called - CurrentBars: M1={CurrentBars[0]}, M3={CurrentBars[1]}, M9={CurrentBars[2]}");
                }

                    // Update phantom M3 bars and calculate values
                    UpdatePhantomM3Bars();

                    // Update phantom M9 bars and calculate values
                    UpdatePhantomM9Bars();

                    // Chart timeframe MACD move tracking is now handled by stochastic cycle detection
                    // (removed direct call here to avoid the old broken logic that increments every bar)

                    // Calculate higher timeframe trends (replaces file reading)
                    CalculateHigherTimeframeTrends();

                    // Calculate SMA cycles for main timeframe (replaces file reading)
                    CalculateSMACycles();

                    // Check if price closed beyond last counted extreme (for bounce counting)
                    if (lastCountedExtremeHigh > 0 && Close[0] > lastCountedExtremeHigh)
                    {
                        closedAboveLastExtremeHigh = true;
                        if (ShowDebugInfo)
                            Print($"✅ Close {Close[0]:F2} > Last Counted Extreme High {lastCountedExtremeHigh:F2} - closedAboveLastExtremeHigh = true");
                    }

                    if (lastCountedExtremeLow > 0 && Close[0] < lastCountedExtremeLow)
                    {
                        closedBelowLastExtremeLow = true;
                        if (ShowDebugInfo)
                            Print($"✅ Close {Close[0]:F2} < Last Counted Extreme Low {lastCountedExtremeLow:F2} - closedBelowLastExtremeLow = true");
                    }

                    // DEBUG: Track M3/M9 indicator updates to verify intrabar calculation  
                    if (ShowDebugInfo && (macdM3 != null && stochM3 != null && macdM9 != null && stochM9 != null))
                    {
                        // INVESTIGATE: Compare different MACD access methods to detect data series confusion
                        double m3MacdDefault = phantomM3MacdLine;
                        double m3MacdDirect = macdM3[0];
                        double m9MacdDefault = phantomM9MacdLine;
                        double m9MacdDirect = macdM9[0];
                        
                        // Check for access method discrepancies
                        bool m3MacdMismatch = Math.Abs(m3MacdDefault - m3MacdDirect) > 0.0001;
                        bool m9MacdMismatch = Math.Abs(m9MacdDefault - m9MacdDirect) > 0.0001;
                        
                        // Check for cross-series contamination (M3 values matching M9)
                        bool m3MatchesM9Default = Math.Abs(m3MacdDefault - m9MacdDefault) < 0.0001;
                        bool m3MatchesM9Direct = Math.Abs(m3MacdDirect - m9MacdDirect) < 0.0001;
                        
                        if (m3MacdMismatch || m9MacdMismatch || m3MatchesM9Default || m3MatchesM9Direct)
                        {
                            Print($"🚨 {Time[0]:HH:mm:ss} MACD ACCESS ANOMALY DETECTED:");
                            Print($"   M3 MACD - Default:{m3MacdDefault:F4} Direct:{m3MacdDirect:F4} {(m3MacdMismatch ? "❌ MISMATCH" : "✅ Match")}");
                            Print($"   M9 MACD - Default:{m9MacdDefault:F4} Direct:{m9MacdDirect:F4} {(m9MacdMismatch ? "❌ MISMATCH" : "✅ Match")}");
                            Print($"   Cross-contamination: M3=M9? Default:{(m3MatchesM9Default ? "❌ YES" : "✅ No")} Direct:{(m3MatchesM9Direct ? "❌ YES" : "✅ No")}");
                        }
                        
                        // Use NT8 values for monitoring (not adjusted values which are only for trading decisions)
                        double currentM3MACD = m3MacdDefault;
                        double currentM3StochK = phantomM3StochK;
                        double currentM9MACD = m9MacdDefault;
                        double currentM9StochK = phantomM9StochK;
                        
                        // Check if values changed (indicating update)
                        bool m3MacdChanged = Math.Abs(currentM3MACD - prevM3MACD) > 0.0001;
                        bool m3StochChanged = Math.Abs(currentM3StochK - prevM3StochK) > 0.01;
                        bool m9MacdChanged = Math.Abs(currentM9MACD - prevM9MACD) > 0.0001;
                        bool m9StochChanged = Math.Abs(currentM9StochK - prevM9StochK) > 0.01;
                        
                        if (m3MacdChanged || m3StochChanged || m9MacdChanged || m9StochChanged)
                        {
                            Print($"📊 MOMENTUM INDICATOR UPDATES at {Time[0]:HH:mm:ss}:");
                            Print($"   M3 MACD: {(m3MacdChanged ? "CHANGED" : "SAME")} {prevM3MACD:F4} → {currentM3MACD:F4}");
                            Print($"   M3 %K: {(m3StochChanged ? "CHANGED" : "SAME")} {prevM3StochK:F1} → {currentM3StochK:F1}");
                            Print($"   M9 MACD: {(m9MacdChanged ? "CHANGED" : "SAME")} {prevM9MACD:F4} → {currentM9MACD:F4}");
                            Print($"   M9 %K: {(m9StochChanged ? "CHANGED" : "SAME")} {prevM9StochK:F1} → {currentM9StochK:F1}");
                            Print($"   M3 BarTime: {(CurrentBars[1] > 0 ? Times[1][0].ToString("HH:mm:ss") : "N/A")}");
                            Print($"   M9 BarTime: {(CurrentBars[2] > 0 ? Times[2][0].ToString("HH:mm:ss") : "N/A")}");
                        }
                        
                        // Store current values for next comparison
                        prevM3MACD = currentM3MACD;
                        prevM3StochK = currentM3StochK;
                        prevM9MACD = currentM9MACD;
                        prevM9StochK = currentM9StochK;
                    }

                // Calculate typical price for M1
                typicalPrice[0] = (High[0] + Low[0] + Close[0]) / 3.0;

                // Debug: Print basic status every 100 bars
                if (CurrentBar % 100 == 0)
                {
                    Print($"📊 Bar {CurrentBar} - EMA34High:{ema34High[0]:F2}, EMA34Low:{ema34Low[0]:F2}, Close:{Close[0]:F2}");
                    Print($"   ShowSignals:{ShowSignals}, currentBounceDataReady:{currentBounceDataReady}");
                    Print($"   longTrackingExtreme:{longTrackingExtreme}, shortTrackingExtreme:{shortTrackingExtreme}");
                }

                // Debug: Print when tracking starts
                if (!longTrackingExtreme && Close[0] > ema34High[0] && Low[0] <= ema34High[0])
                {
                    Print($"🟢 Bar {CurrentBar} - LONG pullback detected! Close:{Close[0]:F2} > EMA34High:{ema34High[0]:F2}");
                }
                if (!shortTrackingExtreme && Close[0] < ema34Low[0] && High[0] >= ema34Low[0])
                {
                    Print($"🔴 Bar {CurrentBar} - SHORT pullback detected! Close:{Close[0]:F2} < EMA34Low:{ema34Low[0]:F2}");
                }

                // Check entry conditions for both long and short
                CheckLongEntryConditions();
                CheckShortEntryConditions();
            }
            catch (Exception ex)
            {
                // Log errors for debugging
                Print($"❌ ERROR in OnBarUpdate at {Time[0]:HH:mm:ss}: {ex.Message}");
                Print($"   Stack: {ex.StackTrace}");
            }
        }
        
        private void CheckLongEntryConditions()
        {
            if (!ShowSignals)
            {
                if (CurrentBar % 100 == 0)
                    Print($"⚠️ ShowSignals is FALSE - no signals will be generated");
                return;
            }

            if (ShowDebugInfo)
            {
                Print($"🔍 CheckLongEntryConditions called at {Time[0]:HH:mm:ss}");
            }
                
            // Get current values
            double currentEMA34High = ema34High[0];
            double currentEMA34Low = ema34Low[0];
            double currentSMA5 = sma5Typical[0];
            double currentClose = Close[0];
            double currentLow = Low[0];
            double currentHigh = High[0];
            
            // CRITICAL INVALIDATION CHECKS (from TrendlineAfterPullbackWriter)
            
            // 1. Check if 5MA crossed to wrong side of wave - COMPLETE RESET
            if (currentSMA5 < currentEMA34Low)
            {
                if (ShowDebugInfo && isInLongPullbackPhase)
                {
                    Print($"❌ {Time[0]:HH:mm:ss} LONG Setup INVALIDATED: 5MA ({currentSMA5:F2}) crossed below EMA34Low ({currentEMA34Low:F2})");
                }
                ResetLongEntryState();
                return;
            }
            
            // 2. Check closes against trend (below EMA34Low) - count and reset if too many
            if (currentClose < currentEMA34Low)
            {
                longClosesAgainstCount++;
                if (longClosesAgainstCount >= MaxClosesAgainstTrend)
                {
                    if (ShowDebugInfo)
                    {
                        Print($"❌ {Time[0]:HH:mm:ss} LONG Setup INVALIDATED: {longClosesAgainstCount} closes below EMA34Low (max: {MaxClosesAgainstTrend})");
                    }
                    ResetLongEntryState();
                    return;
                }
            }
            else
            {
                longClosesAgainstCount = 0; // Reset counter if not closing against trend
            }
            
            // 3. Check extreme break during pullback phase - but respect trigger attempts and retry periods
            if (isInLongPullbackPhase && currentHigh > longExtremePrice)
            {
                // UNCONDITIONAL DEBUG: Extreme break detected
                Print($"🚨 LONG EXTREME BREAK at {Time[0]:HH:mm:ss}!");
                Print($"   currentHigh:{currentHigh:F2} > longExtremePrice:{longExtremePrice:F2}");
                Print($"   longTriggerAttempted:{longTriggerAttempted}, longRetryActive:{longRetryActive}");

                // Determine if we should ignore this extreme break
                bool shouldIgnoreBreak = false;
                string ignoreReason = "";

                // Rule 1: If no trigger attempt has been made yet, allow extreme break to reset
                if (!longTriggerAttempted)
                {
                    shouldIgnoreBreak = false;
                    ignoreReason = "No trigger attempt made yet";
                }
                // Rule 2: If retry is active, ignore extreme break
                else if (longRetryActive)
                {
                    shouldIgnoreBreak = true;
                    ignoreReason = "Retry period active - ignoring extreme break";
                }
                // Rule 3: If this bar is the first trigger attempt, ignore extreme break
                else
                {
                    // Check if this bar will make a trigger attempt
                    double atrBuffer = atr[0] * (ATRBufferPercent / 100.0);
                    bool aboveSMA5 = currentClose > (currentSMA5 + atrBuffer);
                    bool aboveWave = currentClose > (currentEMA34High + atrBuffer);
                    int barsSinceHighest = CurrentBar - longHighestBarIndex;
                    bool minBarsCheck = barsSinceHighest >= MinBarsToTrigger;
                    
                    bool isFirstTriggerAttempt = aboveSMA5 && aboveWave && minBarsCheck && !longTriggerAttempted;
                    
                    if (isFirstTriggerAttempt)
                    {
                        shouldIgnoreBreak = true;
                        ignoreReason = "This bar is first trigger attempt - ignoring extreme break";
                    }
                    else
                    {
                        shouldIgnoreBreak = false;
                        ignoreReason = "No active trigger protection - allowing extreme break reset";
                    }
                }
                
                if (ShowDebugInfo)
                {
                    Print($"🚀 {Time[0]:HH:mm:ss} LONG EXTREME HIGH BROKEN! {currentHigh:F2} > {longExtremePrice:F2}");
                    Print($"🤔 Extreme break analysis: {ignoreReason}");
                    Print($"   TriggerAttempted: {longTriggerAttempted}, RetryActive: {longRetryActive}");
                }
                
                if (shouldIgnoreBreak)
                {
                    // UNCONDITIONAL DEBUG
                    Print($"⚡ LONG Extreme break IGNORED - {ignoreReason}");
                    // Just update the extreme price but don't reset the setup
                    longExtremePrice = currentHigh;
                    longExtremeTime = Time[0];
                    longPullbackHighWaterMark = currentHigh; // Legacy compatibility
                    longHighestBarIndex = CurrentBar;
                }
                else
                {
                    // UNCONDITIONAL DEBUG
                    Print($"🔄 LONG PULLBACK RESET - {ignoreReason}");
                    Print($"   isInLongPullbackPhase: True → False");
                    Print($"   hasEMAHighTouched: True → False");

                    // Reset to Step 1 with new extreme (like TrendlineAfterPullbackWriter)
                    isInLongPullbackPhase = false;
                    hasEMAHighTouched = false;
                    longPullbackConfirmed = false;
                    longInfoBoxCreated = false;
                    
                    // Update extreme tracking
                    longExtremeTime = Time[0];
                    longExtremePrice = currentHigh;
                    longPullbackHighWaterMark = currentHigh; // Legacy compatibility
                    longHighestBarIndex = CurrentBar;
                    
                    // Continue tracking trend (don't reset barsAboveEMAHigh counter)
                    if (ShowDebugInfo)
                    {
                        Print($"📈 {Time[0]:HH:mm:ss} LONG New extreme HIGH: {longExtremePrice:F2} - looking for new pullback");
                    }
                }
            }
            
            // Phase 1: Track uptrend setup (bars with lows above EMA)
            if (Low[0] > currentEMA34High)
            {
                barsAboveEMAHigh++;
                if (!isInLongPullbackPhase && barsAboveEMAHigh >= MinBarsAboveEMA)
                {
                    // START TRACKING HIGHEST HIGH since Step 1 completion (like TrendlineAfterPullbackWriter)
                    if (!longTrackingExtreme)
                    {
                        longTrackingExtreme = true;
                        longExtremeTime = Time[0];
                        longExtremePrice = currentHigh;
                        
                        if (ShowDebugInfo)
                        {
                            Print($"📈 {Time[0]:HH:mm:ss} LONG Started tracking extreme - Initial high: {longExtremePrice:F2}");
                        }
                    }
                    
                    // Update extreme if current high is higher
                    if (longTrackingExtreme && currentHigh > longExtremePrice)
                    {
                        longExtremeTime = Time[0];
                        longExtremePrice = currentHigh;
                        
                        if (ShowDebugInfo)
                        {
                            Print($"📈 {Time[0]:HH:mm:ss} LONG New HIGHEST HIGH: {longExtremePrice:F2}");
                        }
                    }
                    
                    // Legacy tracking for compatibility
                    if (High[0] > longPullbackHighWaterMark)
                    {
                        longPullbackHighWaterMark = High[0];
                        longHighestBarIndex = CurrentBar; // Track bar index of highest price
                    }
                    
                    if (ShowDebugInfo)
                    {
                        Print($"📈 {Time[0]:HH:mm:ss} LONG Uptrend setup: {barsAboveEMAHigh} bars above EMA34High, Extreme: {longExtremePrice:F2}");
                    }
                }
            }
            else
            {
                // Reset if we break the uptrend before pullback
                if (!isInLongPullbackPhase && barsAboveEMAHigh < MinBarsAboveEMA)
                {
                    barsAboveEMAHigh = 0;
                    longPullbackHighWaterMark = 0;
                }
            }
            
            // Phase 2: EMA touch detection (enter pullback phase)
            if (barsAboveEMAHigh >= MinBarsAboveEMA && !isInLongPullbackPhase)
            {
                // Check if we're touching the EMA (low at or below EMA, high at or above EMA)
                bool touchingEMA = (currentLow <= currentEMA34High) && (currentHigh >= currentEMA34High);
                
                if (touchingEMA)
                {
                    isInLongPullbackPhase = true;
                    hasEMAHighTouched = true;
                    longPullbackConfirmed = true; // Mark pullback as confirmed when EMA touched
                    longPullbackStartTime = Time[0]; // Capture precise pullback start time (like SetupState.PullbackStartTime)
                    
                    if (ShowDebugInfo)
                    {
                        Print($"🎯 LONG EMA Touch detected at {Time[0]:HH:mm:ss} - Entering pullback phase");
                        Print($"   EMA34High: {currentEMA34High:F2}, Low: {currentLow:F2}, High: {currentHigh:F2}");
                        Print($"📊 LONG Extreme HIGH tracked: {longExtremeTime:HH:mm} @ {longExtremePrice:F2}");
                    }
                }
            }
            
            // Phase 3: Entry trigger after EMA touch
            // UNCONDITIONAL DEBUG: Track when we're checking for bounce completion
            if (isInLongPullbackPhase || hasEMAHighTouched || longTrackingExtreme)
            {
                Print($"🔍 LONG TRIGGER CHECK at {Time[0]:HH:mm:ss}:");
                Print($"   longTrackingExtreme: {longTrackingExtreme}");
                Print($"   isInLongPullbackPhase: {isInLongPullbackPhase}");
                Print($"   hasEMAHighTouched: {hasEMAHighTouched}");
                Print($"   Combined condition: {(isInLongPullbackPhase && hasEMAHighTouched)}");
            }

            if (isInLongPullbackPhase && hasEMAHighTouched)
            {
                // Calculate ATR buffer (like TrendlineAfterPullbackWriter)
                double atrBuffer = atr[0] * (ATRBufferPercent / 100.0);

                // Entry trigger: Close above BOTH 5MA and EMA34High with ATR buffer (proper TrendlineAfterPullbackWriter logic)
                bool aboveSMA5 = currentClose > (currentSMA5 + atrBuffer);
                bool aboveWave = currentClose > (currentEMA34High + atrBuffer);

                // UNCONDITIONAL DEBUG: Show bounce trigger conditions
                Print($"🎯 LONG Phase 3 BOUNCE TRIGGER CHECK at {Time[0]:HH:mm:ss}:");
                Print($"   ATR={atr[0]:F4}, Buffer%={ATRBufferPercent:F1}, BufferValue={atrBuffer:F4}");
                Print($"   Close:{currentClose:F2} > SMA5+Buffer:{(currentSMA5 + atrBuffer):F2} = {aboveSMA5}");
                Print($"   Close:{currentClose:F2} > EMA34High+Buffer:{(currentEMA34High + atrBuffer):F2} = {aboveWave}");

                // Check minimum bars between highest price and trigger
                int barsSinceHighest = CurrentBar - longHighestBarIndex;

                // Validate bar index for live data transitions
                if (barsSinceHighest < 0 || barsSinceHighest > 10000)
                {
                    if (ShowDebugInfo)
                        Print($"⚠️ LONG Invalid bar index detected (CurrentBar={CurrentBar}, HighestBarIndex={longHighestBarIndex}, Diff={barsSinceHighest}) - resetting tracking");

                    // Reset tracking to current bar
                    longHighestBarIndex = CurrentBar;
                    longExtremePrice = High[0];
                    longExtremeTime = Time[0];
                    barsSinceHighest = 0;
                }

                bool minBarsCheck = barsSinceHighest >= MinBarsToTrigger;

                // UNCONDITIONAL DEBUG: Show MinBars check
                Print($"   BarsSinceHighest:{barsSinceHighest} >= MinRequired:{MinBarsToTrigger} = {minBarsCheck}");
                Print($"   ALL CONDITIONS MET: {(aboveSMA5 && aboveWave && minBarsCheck)}");

                if (aboveSMA5 && aboveWave && minBarsCheck)
                {
                    // Draw bounce arrow if enabled (regardless of momentum filters)
                    if (ShowBounceArrows)
                    {
                        Draw.Text(this, "LongBounce" + CurrentBar, false, "▲", 0, Low[0] - (atr[0] * 0.5), 0, Brushes.Cyan, new SimpleFont("Arial", 20), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                    }

                    // Increment bounce counter if aligned with SMA direction (only count long bounces when SMA is Up)
                    // Only count once per bar to avoid double-counting
                    // Only count if this is first bounce OR price closed above last counted extreme high
                    if (currentSMADirection == "Up" && CurrentBar != lastBounceCountedBar)
                    {
                        bool shouldCount = (bouncesInCurrentSMADirection == 0) || closedAboveLastExtremeHigh;

                        if (shouldCount)
                        {
                            bouncesInCurrentSMADirection++;
                            lastBounceCountedBar = CurrentBar;
                            lastCountedExtremeHigh = longExtremePrice; // Update extreme for next bounce
                            closedAboveLastExtremeHigh = false; // Reset flag

                            if (ShowDebugInfo)
                                Print($"📈 LONG bounce COUNTED - Bounce #{bouncesInCurrentSMADirection} in current SMA Up direction (Extreme High: {lastCountedExtremeHigh:F2})");
                        }
                        else
                        {
                            lastBounceCountedBar = CurrentBar; // Still mark this bar to prevent double-checking
                            if (ShowDebugInfo)
                                Print($"📈 LONG bounce detected but NOT COUNTED - Price hasn't closed above extreme high {lastCountedExtremeHigh:F2} yet (Counter stays at #{bouncesInCurrentSMADirection})");
                        }

                        // Draw bounce counter number if enabled
                        if (ShowBounceCounter)
                        {
                            Draw.Text(this, "LongBounceCount" + CurrentBar, false, bouncesInCurrentSMADirection.ToString(), 0, Low[0] - (atr[0] * 1.0), 0, Brushes.Black, new SimpleFont("Arial", 12), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                        }
                    }

                    // Mark that a trigger attempt has been made (protects from extreme break reset)
                    longTriggerAttempted = true;

                    if (ShowDebugInfo)
                    {
                        Print($"🎯 LONG ENTRY TRIGGER MET at {Time[0]:HH:mm:ss}");
                        Print($"   All price/timing criteria satisfied - checking momentum filters...");
                        Print($"   TriggerAttempted flag set to TRUE (protects from extreme break reset)");
                        Print($"═══════════════════════════════════════════════════");
                    }

                    // Calculate and store bounce validation results (ONCE per bounce)
                    if (!currentBounceDataReady)
                    {
                        // Store bounce info
                        currentBounceIsLong = true;
                        currentBounceEntry = Close[0];

                        // Check multi-timeframe momentum filters (ONCE) - only if enabled
                        currentBounceM3Pass = EnableM3Filter ? CheckM3MomentumFilters(true) : true;
                        currentBounceM9Pass = EnableM9Filter ? CheckM9MomentumFilters(true) : true;
                        
                        // Calculate trade levels (ONCE)
                        currentBounceStopLoss = CalculateStopLossPrice(true);
                        if (currentBounceStopLoss > 0)
                        {
                            currentBounceTarget = currentBounceEntry + (RiskRewardRatio * Math.Abs(currentBounceEntry - currentBounceStopLoss));
                            CalculateLongPositionSizing(currentBounceEntry, currentBounceStopLoss);
                            currentBounceSize = calculatedPositionSize;
                            
                            // Validate EMA34SR (if enabled)
                            if (IsEMA34SREnabled())
                            {
                                currentBounceSRPass = ValidateTradeWithSRFiltering(true, currentBounceEntry, currentBounceStopLoss, currentBounceTarget);
                            }
                            
                            // Validate EMA buffers (if enabled)
                            if (IsEMABuffersEnabled())
                            {
                                string bufferReason;
                                currentBounceBufferPass = !IsEntryBlockedByEMABuffers(currentBounceEntry, true, out bufferReason);
                                if (!currentBounceBufferPass)
                                    currentBounceRejectionReason = bufferReason;
                            }

                            // Validate SMA cycles (if enabled)
                            if (EnableSMACycleFiltering)
                            {
                                var smaCycleResult = GetDirectSMACycleData();
                                string smaCycleDirection = smaCycleResult.Item1;
                                int cycleCount = smaCycleResult.Item2;
                                bool isValid = smaCycleResult.Item3;
                                if (isValid && cycleCount > MaxSMACycles)
                                {
                                    currentBounceSMACyclePass = false;
                                    currentBounceRejectionReason = $"SMA Cycle limit exceeded: {cycleCount} > {MaxSMACycles} (Direction: {smaCycleDirection})";
                                }
                                else
                                {
                                    currentBounceSMACyclePass = true;
                                }
                            }

                            // Validate bounce count (if enabled)
                            if (EnableBounceCountFilter)
                            {
                                if (currentSMADirection == "Up" && bouncesInCurrentSMADirection > MaxBouncesInSMADirection)
                                {
                                    currentBounceBounceCountPass = false;
                                    currentBounceRejectionReason = $"Bounce count limit exceeded: {bouncesInCurrentSMADirection} > {MaxBouncesInSMADirection} (SMA: {currentSMADirection})";
                                    if (ShowDebugInfo)
                                        Print($"❌ LONG rejected by Bounce Count filter: {bouncesInCurrentSMADirection} bounces > {MaxBouncesInSMADirection} limit");
                                }
                                else
                                {
                                    currentBounceBounceCountPass = true;
                                    if (ShowDebugInfo && currentSMADirection == "Up")
                                        Print($"✅ Bounce Count filter passed: {bouncesInCurrentSMADirection} bounces <= {MaxBouncesInSMADirection} limit");
                                }
                            }

                            // Validate SMA direction (if enabled) - Simple filter: Longs only when SMA up
                            if (EnableSMADirectionFilter)
                            {
                                if (currentSMADirection != "Up")
                                {
                                    currentBounceSMADirectionPass = false;
                                    currentBounceRejectionReason = $"SMA Direction filter: Long rejected, SMA is {currentSMADirection} (requires Up)";
                                    if (ShowDebugInfo)
                                        Print($"❌ LONG rejected by SMA Direction filter: SMA is {currentSMADirection}, need Up");
                                }
                                else
                                {
                                    currentBounceSMADirectionPass = true;
                                    if (ShowDebugInfo)
                                        Print($"✅ SMA Direction filter passed: SMA is Up");
                                }
                            }

                            // Validate MACD direction (if enabled) - Simple filter: Longs only when MACD trend up
                            if (EnableMACDDirectionFilter)
                            {
                                if (chartMACDTrendState.currentTrend != MomentumTrend.Up)
                                {
                                    currentBounceMACDDirectionPass = false;
                                    currentBounceRejectionReason = $"MACD Direction filter: Long rejected, MACD trend is {chartMACDTrendState.currentTrend} (requires Up)";
                                    if (ShowDebugInfo)
                                        Print($"❌ LONG rejected by MACD Direction filter: MACD trend is {chartMACDTrendState.currentTrend}, need Up");
                                }
                                else
                                {
                                    currentBounceMACDDirectionPass = true;
                                    if (ShowDebugInfo)
                                        Print($"✅ MACD Direction filter passed: MACD trend is Up");
                                }
                            }

                            // Validate MACD moves (if enabled)
                            if (EnableMACDMoveFiltering)
                            {
                                var macdMoveResult = GetDirectChartMACDMoveData();
                                string macdMoveDirection = macdMoveResult.Item1;
                                int moveCount = macdMoveResult.Item2;
                                bool isValid = macdMoveResult.Item3;
                                if (isValid && moveCount > MaxMACDMoves)
                                {
                                    currentBounceMACDMovePass = false;
                                    currentBounceRejectionReason = $"MACD Move limit exceeded: {moveCount} > {MaxMACDMoves} (Direction: {macdMoveDirection})";
                                }
                                else
                                {
                                    currentBounceMACDMovePass = true;
                                }
                            }

                            // Check trend filtering if enabled
                            if (IsTrendFilteringEnabled())
                            {
                                var trendValidationResult = ValidateTrendFiltering(currentBounceIsLong);
                                bool trendAllowed = trendValidationResult.Item1;
                                string trendReason = trendValidationResult.Item2;
                                currentBounceTrendFilterPass = trendAllowed;
                                if (!trendAllowed)
                                    currentBounceRejectionReason = trendReason;
                            }
                        }
                        
                        currentBounceDataReady = true;
                        string bounceDirection = currentBounceIsLong ? "LONG" : "SHORT";
                        Print($"✅ {bounceDirection} BOUNCE COMPLETE at Bar {CurrentBar}, Time {Time[0]:HH:mm:ss}");
                        Print($"   M3Pass:{currentBounceM3Pass}, M9Pass:{currentBounceM9Pass}, SRPass:{currentBounceSRPass}");
                        Print($"   BufferPass:{currentBounceBufferPass}, TrendFilterPass:{currentBounceTrendFilterPass}");
                    }

                    // Create bounce criteria info box showing all timeframe trend data
                    if (ShowBounceCriteria && currentBounceDataReady)
                    {
                        CreateBounceCriteriaInfoBox(currentBounceIsLong); // Use stored bounce direction
                    }

                    // Use stored results
                    bool m3FiltersPass = currentBounceM3Pass;
                    bool m9FiltersPass = currentBounceM9Pass;
                    
                    if (ShowDebugInfo)
                    {
                        Print($"═══════════════════════════════════════════════════");
                        Print($"🔎 MOMENTUM FILTER SUMMARY:");
                        Print($"   M3 (BOTH required): {(m3FiltersPass ? "✅ PASS" : "❌ FAIL")}");
                        Print($"   M9 (EITHER required): {(m9FiltersPass ? "✅ PASS" : "❌ FAIL")}");
                        Print($"   Final Decision: {(m3FiltersPass && m9FiltersPass ? "✅ SIGNAL APPROVED" : "❌ SIGNAL BLOCKED")}");
                        Print($"═══════════════════════════════════════════════════");
                    }
                    
                    // DEBUG: Show all flag states before box creation decision
                    if (ShowDebugInfo)
                    {
                        Print($"🔍 LONG BOX CREATION CHECK at {Time[0]:HH:mm:ss}:");
                        Print($"   longInfoBoxCreated: {longInfoBoxCreated}");
                        Print($"   isInLongPullbackPhase: {isInLongPullbackPhase}");
                        Print($"   hasEMAHighTouched: {hasEMAHighTouched}");
                        Print($"   longTriggerAttempted: {longTriggerAttempted}");
                        Print($"   Decision: {(!longInfoBoxCreated ? "CREATE BOXES" : "SKIP BOXES")}");
                    }
                    
                    // Create boxes for this bounce (only once per bounce sequence)
                    if (!longInfoBoxCreated)
                    {
                        // Store trigger bar information
                        longTriggerBarIndex = CurrentBar;
                        longTriggerBarLow = currentLow;
                        longTriggerBarHigh = currentHigh;
                        
                        // Create info box using stored results
                        if (ShowInfoBoxes)
                        {
                            if (ShowDebugInfo)
                            {
                                Print($"📦 Creating LONG info box at {Time[0]:HH:mm:ss} - M3:{m3FiltersPass}, M9:{m9FiltersPass}");
                            }
                            
                            longInitialInfoBoxId = CreateBounceInfoBox(true, m3FiltersPass, m9FiltersPass, 0, longTriggerBarLow, longTriggerBarHigh, false, 0);
                        }
                        
                        // Create consolidated trade details box using SAME values as info box
                        if (ShowTradeDetails)
                        {
                            CreateConsolidatedTradeDetailsBoxWithMomentumValues(true, m3FiltersPass, m9FiltersPass);
                        }
                        
                        longInfoBoxCreated = true;
                        
                        // CRITICAL FIX: Complete this bounce sequence and reset to allow fresh EMA detection
                        hasEMAHighTouched = false;
                        isInLongPullbackPhase = false;
                        
                        if (ShowDebugInfo)
                        {
                            Print($"🔄 LONG STATE RESET after box creation at {Time[0]:HH:mm:ss}");
                            Print($"   hasEMAHighTouched: true → false");
                            Print($"   isInLongPullbackPhase: true → false");
                            Print($"   longInfoBoxCreated: true → false (allow next bounce)");
                        }
                    }
                    
                    // CRITICAL FIX: Reset box flag OUTSIDE conditional so ALL bounces (first AND secondary) reset it
                    longInfoBoxCreated = false;

                    // Check time and day filtering
                    bool timeFilterPass = IsWithinTradingHours();
                    bool dayFilterPass = IsValidTradingDay();

                    // Check all validation criteria (momentum + EMA34SR + EMA buffers + SMA cycles + bounce count + SMA direction + MACD direction + MACD moves + trend filtering + time + day)
                    bool allValidationsPassed = m3FiltersPass && m9FiltersPass && currentBounceSRPass && currentBounceBufferPass && currentBounceSMACyclePass && currentBounceBounceCountPass && currentBounceSMADirectionPass && currentBounceMACDDirectionPass && currentBounceMACDMovePass && currentBounceTrendFilterPass && timeFilterPass && dayFilterPass;

                    Print($"🔍 LONG VALIDATION CHECK at Bar {CurrentBar}:");
                    Print($"   M3Pass:{m3FiltersPass}, M9Pass:{m9FiltersPass}, SRPass:{currentBounceSRPass}");
                    Print($"   BufferPass:{currentBounceBufferPass}, SMACyclePass:{currentBounceSMACyclePass}, BounceCountPass:{currentBounceBounceCountPass}, SMADirectionPass:{currentBounceSMADirectionPass}");
                    Print($"   MACDDirectionPass:{currentBounceMACDDirectionPass}, MACDMovePass:{currentBounceMACDMovePass}, TrendFilterPass:{currentBounceTrendFilterPass}");
                    Print($"   TimeFilterPass:{timeFilterPass} (Time: {Time[0]:HH:mm:ss})");
                    Print($"   DayFilterPass:{dayFilterPass} (Day: {Time[0]:ddd})");
                    Print($"   ALL PASS: {allValidationsPassed}");

                    if (allValidationsPassed)
                    {
                        // Signal confirmed!
                        Print($"🎯 GENERATING LONG SIGNAL at Bar {CurrentBar}, Time {Time[0]:HH:mm:ss}!");
                        GenerateLongSignal(false, 0); // Not delayed
                    }
                    else
                    {
                        Print($"❌ LONG SIGNAL BLOCKED at Bar {CurrentBar} - One or more validations failed");
                        // Momentum filters failed - start retry mechanism if enabled
                        if (RetryBars > 0 && !longRetryActive)
                        {
                            longRetryActive = true;
                            longRetryStartBar = CurrentBar;
                            longDelayedEntryBars = 0;
                            
                            if (ShowDebugInfo)
                            {
                                Print($"⏳ {Time[0]:HH:mm:ss} LONG Entry blocked - Starting {RetryBars} bar retry period");
                                Print($"   M3 filters: {m3FiltersPass}, M9 filters: {m9FiltersPass}");
                            }
                        }
                        else 
                        {
                            if (ShowDebugInfo)
                            {
                                Print($"❌ {Time[0]:HH:mm:ss} LONG Entry trigger met but momentum filters failed (no retry enabled)");
                                Print($"   M3 filters: {m3FiltersPass}, M9 filters: {m9FiltersPass}");
                            }
                            
                            // Trade details will be shown in consolidated box at trigger bar
                        }
                    }
                }
                
                // Handle retry mechanism for delayed entries
                else if (longRetryActive && RetryBars > 0)
                {
                    int barsSinceRetryStart = CurrentBar - longRetryStartBar;
                    
                    if (barsSinceRetryStart <= RetryBars)
                    {
                        // Still within retry window - use stored momentum results
                        bool m3FiltersPass = currentBounceM3Pass;
                        bool m9FiltersPass = currentBounceM9Pass;
                        
                        if (m3FiltersPass && m9FiltersPass)
                        {
                            longDelayedEntryBars = barsSinceRetryStart;
                            
                            // Remove the initial blocked info box to prevent overlap
                            if (!string.IsNullOrEmpty(longInitialInfoBoxId))
                            {
                                RemoveDrawObject(longInitialInfoBoxId);
                                
                                if (ShowDebugInfo)
                                {
                                    Print($"🗑️ {Time[0]:HH:mm:ss} LONG Removed initial blocked info box: {longInitialInfoBoxId}");
                                }
                            }
                            
                            if (ShowDebugInfo)
                            {
                                Print($"✅ {Time[0]:HH:mm:ss} LONG Delayed entry SUCCESS after {longDelayedEntryBars} bars!");
                            }
                            
                            GenerateLongSignal(true, longDelayedEntryBars); // Delayed entry
                        }
                        else if (ShowDebugInfo && barsSinceRetryStart == 1)
                        {
                            Print($"⏳ {Time[0]:HH:mm:ss} LONG Retry attempt {barsSinceRetryStart}/{RetryBars} - filters still failing");
                        }
                    }
                    else
                    {
                        // Retry period expired
                        longRetryActive = false;
                        
                        if (ShowDebugInfo)
                        {
                            Print($"⏰ {Time[0]:HH:mm:ss} LONG Retry period expired ({RetryBars} bars) - no delayed entry");
                        }
                        
                        // Trade details will be shown in consolidated box at trigger bar
                    }
                }
            }
            
            // Reset conditions if we move too far from setup
            if (isInLongPullbackPhase && currentLow > currentEMA34High + (currentEMA34High * 0.02)) // 2% above EMA
            {
                ResetLongEntryState();
                if (ShowDebugInfo)
                {
                    Print($"🔄 {Time[0]:HH:mm:ss} LONG Reset: Moved too far above EMA ({currentLow:F2} > {currentEMA34High:F2})");
                }
            }
        }

        private void CheckShortEntryConditions()
        {
            if (!ShowSignals)
                return;

            if (ShowDebugInfo)
            {
                Print($"🔍 CheckShortEntryConditions called at {Time[0]:HH:mm:ss}");
            }
                
            // Get current values
            double currentEMA34High = ema34High[0];
            double currentEMA34Low = ema34Low[0];
            double currentSMA5 = sma5Typical[0];
            double currentClose = Close[0];
            double currentLow = Low[0];
            double currentHigh = High[0];
            
            // CRITICAL INVALIDATION CHECKS (from TrendlineAfterPullbackWriter)
            
            // 1. Check if 5MA crossed to wrong side of wave - COMPLETE RESET
            if (currentSMA5 > currentEMA34High)
            {
                if (ShowDebugInfo && isInShortPullbackPhase)
                {
                    Print($"❌ {Time[0]:HH:mm:ss} SHORT Setup INVALIDATED: 5MA ({currentSMA5:F2}) crossed above EMA34High ({currentEMA34High:F2})");
                }
                ResetShortEntryState();
                return;
            }
            
            // 2. Check closes against trend (above EMA34High) - count and reset if too many
            if (currentClose > currentEMA34High)
            {
                shortClosesAgainstCount++;
                if (shortClosesAgainstCount >= MaxClosesAgainstTrend)
                {
                    if (ShowDebugInfo)
                    {
                        Print($"❌ {Time[0]:HH:mm:ss} SHORT Setup INVALIDATED: {shortClosesAgainstCount} closes above EMA34High (max: {MaxClosesAgainstTrend})");
                    }
                    ResetShortEntryState();
                    return;
                }
            }
            else
            {
                shortClosesAgainstCount = 0; // Reset counter if not closing against trend
            }
            
            // 3. Check extreme break during pullback phase - but respect trigger attempts and retry periods
            if (isInShortPullbackPhase && currentLow < shortExtremePrice)
            {
                // UNCONDITIONAL DEBUG: Extreme break detected
                Print($"🚨 SHORT EXTREME BREAK at {Time[0]:HH:mm:ss}!");
                Print($"   currentLow:{currentLow:F2} < shortExtremePrice:{shortExtremePrice:F2}");
                Print($"   shortTriggerAttempted:{shortTriggerAttempted}, shortRetryActive:{shortRetryActive}");

                // Determine if we should ignore this extreme break
                bool shouldIgnoreBreak = false;
                string ignoreReason = "";

                // Rule 1: If no trigger attempt has been made yet, allow extreme break to reset
                if (!shortTriggerAttempted)
                {
                    shouldIgnoreBreak = false;
                    ignoreReason = "No trigger attempt made yet";
                }
                // Rule 2: If retry is active, ignore extreme break
                else if (shortRetryActive)
                {
                    shouldIgnoreBreak = true;
                    ignoreReason = "Retry period active - ignoring extreme break";
                }
                // Rule 3: If this bar is the first trigger attempt, ignore extreme break
                else
                {
                    // Check if this bar will make a trigger attempt
                    double atrBuffer = atr[0] * (ATRBufferPercent / 100.0);
                    bool belowSMA5 = currentClose < (currentSMA5 - atrBuffer);
                    bool belowWave = currentClose < (currentEMA34Low - atrBuffer);
                    int barsSinceLowest = CurrentBar - shortLowestBarIndex;
                    bool minBarsCheck = barsSinceLowest >= MinBarsToTrigger;
                    
                    bool isFirstTriggerAttempt = belowSMA5 && belowWave && minBarsCheck && !shortTriggerAttempted;
                    
                    if (isFirstTriggerAttempt)
                    {
                        shouldIgnoreBreak = true;
                        ignoreReason = "This bar is first trigger attempt - ignoring extreme break";
                    }
                    else
                    {
                        shouldIgnoreBreak = false;
                        ignoreReason = "No active trigger protection - allowing extreme break reset";
                    }
                }
                
                if (ShowDebugInfo)
                {
                    Print($"🚀 {Time[0]:HH:mm:ss} SHORT EXTREME LOW BROKEN! {currentLow:F2} < {shortExtremePrice:F2}");
                    Print($"🤔 Extreme break analysis: {ignoreReason}");
                    Print($"   TriggerAttempted: {shortTriggerAttempted}, RetryActive: {shortRetryActive}");
                }
                
                if (shouldIgnoreBreak)
                {
                    // UNCONDITIONAL DEBUG
                    Print($"⚡ SHORT Extreme break IGNORED - {ignoreReason}");
                    // Just update the extreme price but don't reset the setup
                    shortExtremePrice = currentLow;
                    shortExtremeTime = Time[0];
                    shortPullbackLowWaterMark = currentLow; // Legacy compatibility
                    shortLowestBarIndex = CurrentBar;
                }
                else
                {
                    // UNCONDITIONAL DEBUG
                    Print($"🔄 SHORT PULLBACK RESET - {ignoreReason}");
                    Print($"   isInShortPullbackPhase: True → False");
                    Print($"   hasEMALowTouched: True → False");

                    // Reset to Step 1 with new extreme (like TrendlineAfterPullbackWriter)
                    isInShortPullbackPhase = false;
                    hasEMALowTouched = false;
                    shortPullbackConfirmed = false;
                    shortInfoBoxCreated = false;
                    
                    // Update extreme tracking
                    shortExtremeTime = Time[0];
                    shortExtremePrice = currentLow;
                    shortPullbackLowWaterMark = currentLow; // Legacy compatibility
                    shortLowestBarIndex = CurrentBar;
                    
                    // Continue tracking trend (don't reset barsBelowEMALow counter)
                    if (ShowDebugInfo)
                    {
                        Print($"📉 {Time[0]:HH:mm:ss} SHORT New extreme LOW: {shortExtremePrice:F2} - looking for new pullback");
                    }
                }
            }
            
            // Phase 1: Track downtrend setup (bars with highs below EMA)
            if (High[0] < currentEMA34Low)
            {
                barsBelowEMALow++;
                // UNCONDITIONAL DEBUG: Show counter progress
                Print($"📊 SHORT Phase 1: barsBelowEMALow={barsBelowEMALow}/{MinBarsAboveEMA}, High[0]:{High[0]:F2} < EMA34Low:{currentEMA34Low:F2}");

                if (!isInShortPullbackPhase && barsBelowEMALow >= MinBarsAboveEMA) // Reuse same parameter
                {
                    // START TRACKING LOWEST LOW since Step 1 completion (like TrendlineAfterPullbackWriter)
                    if (!shortTrackingExtreme)
                    {
                        shortTrackingExtreme = true;
                        shortExtremeTime = Time[0];
                        shortExtremePrice = currentLow;
                        
                        if (ShowDebugInfo)
                        {
                            Print($"📉 {Time[0]:HH:mm:ss} SHORT Started tracking extreme - Initial low: {shortExtremePrice:F2}");
                        }
                    }
                    
                    // Update extreme if current low is lower
                    if (shortTrackingExtreme && currentLow < shortExtremePrice)
                    {
                        shortExtremeTime = Time[0];
                        shortExtremePrice = currentLow;
                        
                        if (ShowDebugInfo)
                        {
                            Print($"📉 {Time[0]:HH:mm:ss} SHORT New LOWEST LOW: {shortExtremePrice:F2}");
                        }
                    }
                    
                    // Legacy tracking for compatibility
                    double currentLWM = shortPullbackLowWaterMark == 0 ? Low[0] : shortPullbackLowWaterMark;
                    if (Low[0] < currentLWM)
                    {
                        shortPullbackLowWaterMark = Low[0];
                        shortLowestBarIndex = CurrentBar; // Track bar index of lowest price
                    }
                    
                    if (ShowDebugInfo)
                    {
                        Print($"📉 {Time[0]:HH:mm:ss} SHORT Downtrend setup: {barsBelowEMALow} bars below EMA34Low, Extreme: {shortExtremePrice:F2}");
                    }
                }
            }
            else
            {
                // UNCONDITIONAL DEBUG: Show why counter resets
                if (shortTrackingExtreme && barsBelowEMALow > 0)
                {
                    Print($"⚠️ SHORT Phase 1 BROKEN: High[0]:{High[0]:F2} >= EMA34Low:{currentEMA34Low:F2}, barsBelowEMALow was {barsBelowEMALow}");
                }

                // Reset if we break the downtrend before pullback
                if (!isInShortPullbackPhase && barsBelowEMALow < MinBarsAboveEMA)
                {
                    barsBelowEMALow = 0;
                    shortPullbackLowWaterMark = 0;
                }
            }
            
            // Phase 2: EMA touch detection (enter pullback phase)
            if (barsBelowEMALow >= MinBarsAboveEMA && !isInShortPullbackPhase)
            {
                // Check if we're touching the EMA (high at or above EMA, low at or below EMA)
                bool touchingEMA = (currentHigh >= currentEMA34Low) && (currentLow <= currentEMA34Low);

                // UNCONDITIONAL DEBUG: Show Phase 2 check
                Print($"📊 SHORT Phase 2 CHECK: barsBelowEMALow={barsBelowEMALow}, touchingEMA={touchingEMA}");
                Print($"   High:{currentHigh:F2} >= EMA34Low:{currentEMA34Low:F2} = {currentHigh >= currentEMA34Low}");
                Print($"   Low:{currentLow:F2} <= EMA34Low:{currentEMA34Low:F2} = {currentLow <= currentEMA34Low}");

                if (touchingEMA)
                {
                    isInShortPullbackPhase = true;
                    hasEMALowTouched = true;
                    shortPullbackConfirmed = true; // Mark pullback as confirmed when EMA touched
                    shortPullbackStartTime = Time[0]; // Capture precise pullback start time (like SetupState.PullbackStartTime)
                    
                    if (ShowDebugInfo)
                    {
                        Print($"🎯 SHORT EMA Touch detected at {Time[0]:HH:mm:ss} - Entering pullback phase");
                        Print($"   EMA34Low: {currentEMA34Low:F2}, Low: {currentLow:F2}, High: {currentHigh:F2}");
                        Print($"📊 SHORT Extreme LOW tracked: {shortExtremeTime:HH:mm} @ {shortExtremePrice:F2}");
                    }
                }
            }
            
            // Phase 3: Entry trigger after EMA touch
            // UNCONDITIONAL DEBUG: Track when we're checking for bounce completion
            if (isInShortPullbackPhase || hasEMALowTouched || shortTrackingExtreme)
            {
                Print($"🔍 SHORT TRIGGER CHECK at {Time[0]:HH:mm:ss}:");
                Print($"   shortTrackingExtreme: {shortTrackingExtreme}");
                Print($"   isInShortPullbackPhase: {isInShortPullbackPhase}");
                Print($"   hasEMALowTouched: {hasEMALowTouched}");
                Print($"   Combined condition: {(isInShortPullbackPhase && hasEMALowTouched)}");
            }

            if (isInShortPullbackPhase && hasEMALowTouched)
            {
                // Calculate ATR buffer (like TrendlineAfterPullbackWriter)
                double atrBuffer = atr[0] * (ATRBufferPercent / 100.0);

                // Entry trigger: Close below BOTH 5MA and EMA34Low with ATR buffer (proper TrendlineAfterPullbackWriter logic)
                bool belowSMA5 = currentClose < (currentSMA5 - atrBuffer);
                bool belowWave = currentClose < (currentEMA34Low - atrBuffer);

                // UNCONDITIONAL DEBUG: Show bounce trigger conditions
                Print($"🎯 SHORT Phase 3 BOUNCE TRIGGER CHECK at {Time[0]:HH:mm:ss}:");
                Print($"   ATR={atr[0]:F4}, Buffer%={ATRBufferPercent:F1}, BufferValue={atrBuffer:F4}");
                Print($"   Close:{currentClose:F2} < SMA5-Buffer:{(currentSMA5 - atrBuffer):F2} = {belowSMA5}");
                Print($"   Close:{currentClose:F2} < EMA34Low-Buffer:{(currentEMA34Low - atrBuffer):F2} = {belowWave}");

                // Check minimum bars between lowest price and trigger
                int barsSinceLowest = CurrentBar - shortLowestBarIndex;

                // Validate bar index for live data transitions
                if (barsSinceLowest < 0 || barsSinceLowest > 10000)
                {
                    if (ShowDebugInfo)
                        Print($"⚠️ SHORT Invalid bar index detected (CurrentBar={CurrentBar}, LowestBarIndex={shortLowestBarIndex}, Diff={barsSinceLowest}) - resetting tracking");

                    // Reset tracking to current bar
                    shortLowestBarIndex = CurrentBar;
                    shortExtremePrice = Low[0];
                    shortExtremeTime = Time[0];
                    barsSinceLowest = 0;
                }

                bool minBarsCheck = barsSinceLowest >= MinBarsToTrigger;

                // UNCONDITIONAL DEBUG: Show MinBars check
                Print($"   BarsSinceLowest:{barsSinceLowest} >= MinRequired:{MinBarsToTrigger} = {minBarsCheck}");
                Print($"   ALL CONDITIONS MET: {(belowSMA5 && belowWave && minBarsCheck)}");

                if (belowSMA5 && belowWave && minBarsCheck)
                {
                    // Draw bounce arrow if enabled (regardless of momentum filters)
                    if (ShowBounceArrows)
                    {
                        Draw.Text(this, "ShortBounce" + CurrentBar, false, "▼", 0, High[0] + (atr[0] * 0.5), 0, Brushes.Magenta, new SimpleFont("Arial", 20), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                    }

                    // Increment bounce counter if aligned with SMA direction (only count short bounces when SMA is Down)
                    // Only count once per bar to avoid double-counting
                    // Only count if this is first bounce OR price closed below last counted extreme low
                    if (currentSMADirection == "Down" && CurrentBar != lastBounceCountedBar)
                    {
                        bool shouldCount = (bouncesInCurrentSMADirection == 0) || closedBelowLastExtremeLow;

                        if (shouldCount)
                        {
                            bouncesInCurrentSMADirection++;
                            lastBounceCountedBar = CurrentBar;
                            lastCountedExtremeLow = shortExtremePrice; // Update extreme for next bounce
                            closedBelowLastExtremeLow = false; // Reset flag

                            if (ShowDebugInfo)
                                Print($"📉 SHORT bounce COUNTED - Bounce #{bouncesInCurrentSMADirection} in current SMA Down direction (Extreme Low: {lastCountedExtremeLow:F2})");
                        }
                        else
                        {
                            lastBounceCountedBar = CurrentBar; // Still mark this bar to prevent double-checking
                            if (ShowDebugInfo)
                                Print($"📉 SHORT bounce detected but NOT COUNTED - Price hasn't closed below extreme low {lastCountedExtremeLow:F2} yet (Counter stays at #{bouncesInCurrentSMADirection})");
                        }

                        // Draw bounce counter number if enabled
                        if (ShowBounceCounter)
                        {
                            Draw.Text(this, "ShortBounceCount" + CurrentBar, false, bouncesInCurrentSMADirection.ToString(), 0, High[0] + (atr[0] * 1.0), 0, Brushes.Black, new SimpleFont("Arial", 12), TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0);
                        }
                    }

                    // Mark that a trigger attempt has been made (protects from extreme break reset)
                    shortTriggerAttempted = true;

                    if (ShowDebugInfo)
                    {
                        Print($"🎯 SHORT ENTRY TRIGGER MET at {Time[0]:HH:mm:ss}");
                        Print($"   All price/timing criteria satisfied - checking momentum filters...");
                        Print($"   TriggerAttempted flag set to TRUE (protects from extreme break reset)");
                        Print($"═══════════════════════════════════════════════════");
                    }

                    // Calculate and store bounce validation results (ONCE per bounce)
                    if (!currentBounceDataReady)
                    {
                        // Store bounce info
                        currentBounceIsLong = false;
                        currentBounceEntry = Close[0];
                        
                        // Check multi-timeframe momentum filters (ONCE) - only if enabled
                        currentBounceM3Pass = EnableM3Filter ? CheckM3MomentumFilters(false) : true;
                        currentBounceM9Pass = EnableM9Filter ? CheckM9MomentumFilters(false) : true;
                        
                        // Calculate trade levels (ONCE)
                        currentBounceStopLoss = CalculateStopLossPrice(false);
                        if (currentBounceStopLoss > 0)
                        {
                            currentBounceTarget = currentBounceEntry - (RiskRewardRatio * Math.Abs(currentBounceEntry - currentBounceStopLoss));
                            CalculateShortPositionSizing(currentBounceEntry, currentBounceStopLoss);
                            currentBounceSize = calculatedPositionSize;
                            
                            // Validate EMA34SR (if enabled)
                            if (IsEMA34SREnabled())
                            {
                                currentBounceSRPass = ValidateTradeWithSRFiltering(false, currentBounceEntry, currentBounceStopLoss, currentBounceTarget);
                            }
                            
                            // Validate EMA buffers (if enabled)
                            if (IsEMABuffersEnabled())
                            {
                                string bufferReason;
                                currentBounceBufferPass = !IsEntryBlockedByEMABuffers(currentBounceEntry, false, out bufferReason);
                                if (!currentBounceBufferPass)
                                    currentBounceRejectionReason = bufferReason;
                            }

                            // Validate SMA cycles (if enabled)
                            if (EnableSMACycleFiltering)
                            {
                                var smaCycleResult = GetDirectSMACycleData();
                                string smaCycleDirection = smaCycleResult.Item1;
                                int cycleCount = smaCycleResult.Item2;
                                bool isValid = smaCycleResult.Item3;
                                if (isValid && cycleCount > MaxSMACycles)
                                {
                                    currentBounceSMACyclePass = false;
                                    currentBounceRejectionReason = $"SMA Cycle limit exceeded: {cycleCount} > {MaxSMACycles} (Direction: {smaCycleDirection})";
                                }
                                else
                                {
                                    currentBounceSMACyclePass = true;
                                }
                            }

                            // Validate bounce count (if enabled)
                            if (EnableBounceCountFilter)
                            {
                                if (currentSMADirection == "Down" && bouncesInCurrentSMADirection > MaxBouncesInSMADirection)
                                {
                                    currentBounceBounceCountPass = false;
                                    currentBounceRejectionReason = $"Bounce count limit exceeded: {bouncesInCurrentSMADirection} > {MaxBouncesInSMADirection} (SMA: {currentSMADirection})";
                                    if (ShowDebugInfo)
                                        Print($"❌ SHORT rejected by Bounce Count filter: {bouncesInCurrentSMADirection} bounces > {MaxBouncesInSMADirection} limit");
                                }
                                else
                                {
                                    currentBounceBounceCountPass = true;
                                    if (ShowDebugInfo && currentSMADirection == "Down")
                                        Print($"✅ Bounce Count filter passed: {bouncesInCurrentSMADirection} bounces <= {MaxBouncesInSMADirection} limit");
                                }
                            }

                            // Validate SMA direction (if enabled) - Simple filter: Shorts only when SMA down
                            if (EnableSMADirectionFilter)
                            {
                                if (currentSMADirection != "Down")
                                {
                                    currentBounceSMADirectionPass = false;
                                    currentBounceRejectionReason = $"SMA Direction filter: Short rejected, SMA is {currentSMADirection} (requires Down)";
                                    if (ShowDebugInfo)
                                        Print($"❌ SHORT rejected by SMA Direction filter: SMA is {currentSMADirection}, need Down");
                                }
                                else
                                {
                                    currentBounceSMADirectionPass = true;
                                    if (ShowDebugInfo)
                                        Print($"✅ SMA Direction filter passed: SMA is Down");
                                }
                            }

                            // Validate MACD direction (if enabled) - Simple filter: Shorts only when MACD trend down
                            if (EnableMACDDirectionFilter)
                            {
                                if (chartMACDTrendState.currentTrend != MomentumTrend.Down)
                                {
                                    currentBounceMACDDirectionPass = false;
                                    currentBounceRejectionReason = $"MACD Direction filter: Short rejected, MACD trend is {chartMACDTrendState.currentTrend} (requires Down)";
                                    if (ShowDebugInfo)
                                        Print($"❌ SHORT rejected by MACD Direction filter: MACD trend is {chartMACDTrendState.currentTrend}, need Down");
                                }
                                else
                                {
                                    currentBounceMACDDirectionPass = true;
                                    if (ShowDebugInfo)
                                        Print($"✅ MACD Direction filter passed: MACD trend is Down");
                                }
                            }

                            // Validate MACD moves (if enabled)
                            if (EnableMACDMoveFiltering)
                            {
                                var macdMoveResult = GetDirectChartMACDMoveData();
                                string macdMoveDirection = macdMoveResult.Item1;
                                int moveCount = macdMoveResult.Item2;
                                bool isValid = macdMoveResult.Item3;
                                if (isValid && moveCount > MaxMACDMoves)
                                {
                                    currentBounceMACDMovePass = false;
                                    currentBounceRejectionReason = $"MACD Move limit exceeded: {moveCount} > {MaxMACDMoves} (Direction: {macdMoveDirection})";
                                }
                                else
                                {
                                    currentBounceMACDMovePass = true;
                                }
                            }

                            // Check trend filtering if enabled
                            if (IsTrendFilteringEnabled())
                            {
                                var trendValidationResult = ValidateTrendFiltering(currentBounceIsLong);
                                bool trendAllowed = trendValidationResult.Item1;
                                string trendReason = trendValidationResult.Item2;
                                currentBounceTrendFilterPass = trendAllowed;
                                if (!trendAllowed)
                                    currentBounceRejectionReason = trendReason;
                            }
                        }
                        
                        currentBounceDataReady = true;
                        string bounceDirection = currentBounceIsLong ? "LONG" : "SHORT";
                        Print($"✅ {bounceDirection} BOUNCE COMPLETE at Bar {CurrentBar}, Time {Time[0]:HH:mm:ss}");
                        Print($"   M3Pass:{currentBounceM3Pass}, M9Pass:{currentBounceM9Pass}, SRPass:{currentBounceSRPass}");
                        Print($"   BufferPass:{currentBounceBufferPass}, TrendFilterPass:{currentBounceTrendFilterPass}");
                    }

                    // Create bounce criteria info box showing all timeframe trend data
                    if (ShowBounceCriteria && currentBounceDataReady)
                    {
                        CreateBounceCriteriaInfoBox(currentBounceIsLong); // Use stored bounce direction
                    }

                    // Use stored results
                    bool m3FiltersPass = currentBounceM3Pass;
                    bool m9FiltersPass = currentBounceM9Pass;
                    
                    if (ShowDebugInfo)
                    {
                        Print($"═══════════════════════════════════════════════════");
                        Print($"🔎 MOMENTUM FILTER SUMMARY:");
                        Print($"   M3 (BOTH required): {(m3FiltersPass ? "✅ PASS" : "❌ FAIL")}");
                        Print($"   M9 (EITHER required): {(m9FiltersPass ? "✅ PASS" : "❌ FAIL")}");
                        Print($"   Final Decision: {(m3FiltersPass && m9FiltersPass ? "✅ SIGNAL APPROVED" : "❌ SIGNAL BLOCKED")}");
                        Print($"═══════════════════════════════════════════════════");
                    }
                    
                    // DEBUG: Show all flag states before box creation decision
                    if (ShowDebugInfo)
                    {
                        Print($"🔍 SHORT BOX CREATION CHECK at {Time[0]:HH:mm:ss}:");
                        Print($"   shortInfoBoxCreated: {shortInfoBoxCreated}");
                        Print($"   isInShortPullbackPhase: {isInShortPullbackPhase}");
                        Print($"   hasEMALowTouched: {hasEMALowTouched}");
                        Print($"   shortTriggerAttempted: {shortTriggerAttempted}");
                        Print($"   Decision: {(!shortInfoBoxCreated ? "CREATE BOXES" : "SKIP BOXES")}");
                    }
                    
                    // Create boxes for this bounce (only once per bounce sequence)
                    if (!shortInfoBoxCreated)
                    {
                        // Store trigger bar information
                        shortTriggerBarIndex = CurrentBar;
                        shortTriggerBarLow = currentLow;
                        shortTriggerBarHigh = currentHigh;
                        
                        // Create info box using stored results
                        if (ShowInfoBoxes)
                        {
                            shortInitialInfoBoxId = CreateBounceInfoBox(false, m3FiltersPass, m9FiltersPass, 0, shortTriggerBarLow, shortTriggerBarHigh, false, 0);
                        }
                        
                        // Create consolidated trade details box using SAME values as info box
                        if (ShowTradeDetails)
                        {
                            CreateConsolidatedTradeDetailsBoxWithMomentumValues(false, m3FiltersPass, m9FiltersPass);
                        }
                        
                        shortInfoBoxCreated = true;
                        
                        // CRITICAL FIX: Complete this bounce sequence and reset to allow fresh EMA detection
                        hasEMALowTouched = false;
                        isInShortPullbackPhase = false;
                        
                        if (ShowDebugInfo)
                        {
                            Print($"🔄 SHORT STATE RESET after box creation at {Time[0]:HH:mm:ss}");
                            Print($"   hasEMALowTouched: true → false");
                            Print($"   isInShortPullbackPhase: true → false");
                            Print($"   shortInfoBoxCreated: true → false (allow next bounce)");
                        }
                    }
                    
                    // CRITICAL FIX: Reset box flag OUTSIDE conditional so ALL bounces (first AND secondary) reset it
                    shortInfoBoxCreated = false;

                    // Check time and day filtering
                    bool timeFilterPass = IsWithinTradingHours();
                    bool dayFilterPass = IsValidTradingDay();

                    // Check all validation criteria (momentum + EMA34SR + EMA buffers + SMA cycles + bounce count + SMA direction + MACD direction + MACD moves + trend filtering + time + day)
                    bool allValidationsPassed = m3FiltersPass && m9FiltersPass && currentBounceSRPass && currentBounceBufferPass && currentBounceSMACyclePass && currentBounceBounceCountPass && currentBounceSMADirectionPass && currentBounceMACDDirectionPass && currentBounceMACDMovePass && currentBounceTrendFilterPass && timeFilterPass && dayFilterPass;

                    Print($"🔍 SHORT VALIDATION CHECK at Bar {CurrentBar}:");
                    Print($"   M3Pass:{m3FiltersPass}, M9Pass:{m9FiltersPass}, SRPass:{currentBounceSRPass}");
                    Print($"   BufferPass:{currentBounceBufferPass}, SMACyclePass:{currentBounceSMACyclePass}, BounceCountPass:{currentBounceBounceCountPass}, SMADirectionPass:{currentBounceSMADirectionPass}");
                    Print($"   MACDDirectionPass:{currentBounceMACDDirectionPass}, MACDMovePass:{currentBounceMACDMovePass}, TrendFilterPass:{currentBounceTrendFilterPass}");
                    Print($"   TimeFilterPass:{timeFilterPass} (Time: {Time[0]:HH:mm:ss})");
                    Print($"   DayFilterPass:{dayFilterPass} (Day: {Time[0]:ddd})");
                    Print($"   ALL PASS: {allValidationsPassed}");

                    if (allValidationsPassed)
                    {
                        // Signal confirmed!
                        Print($"🎯 GENERATING SHORT SIGNAL at Bar {CurrentBar}, Time {Time[0]:HH:mm:ss}!");
                        GenerateShortSignal(false, 0); // Not delayed
                    }
                    else
                    {
                        Print($"❌ SHORT SIGNAL BLOCKED at Bar {CurrentBar} - One or more validations failed");
                        // Momentum filters failed - start retry mechanism if enabled
                        if (RetryBars > 0 && !shortRetryActive)
                        {
                            shortRetryActive = true;
                            shortRetryStartBar = CurrentBar;
                            shortDelayedEntryBars = 0;
                            
                            if (ShowDebugInfo)
                            {
                                Print($"⏳ {Time[0]:HH:mm:ss} SHORT Entry blocked - Starting {RetryBars} bar retry period");
                                Print($"   M3 filters: {m3FiltersPass}, M9 filters: {m9FiltersPass}");
                            }
                        }
                        else 
                        {
                            if (ShowDebugInfo)
                            {
                                Print($"❌ {Time[0]:HH:mm:ss} SHORT Entry trigger met but momentum filters failed (no retry enabled)");
                                Print($"   M3 filters: {m3FiltersPass}, M9 filters: {m9FiltersPass}");
                            }
                            
                            // Trade details will be shown in consolidated box at trigger bar
                        }
                    }
                }
                
                // Handle retry mechanism for delayed entries
                else if (shortRetryActive && RetryBars > 0)
                {
                    int barsSinceRetryStart = CurrentBar - shortRetryStartBar;
                    
                    if (barsSinceRetryStart <= RetryBars)
                    {
                        // Still within retry window - use stored momentum results
                        bool m3FiltersPass = currentBounceM3Pass;
                        bool m9FiltersPass = currentBounceM9Pass;
                        
                        if (m3FiltersPass && m9FiltersPass)
                        {
                            shortDelayedEntryBars = barsSinceRetryStart;
                            
                            // Remove the initial blocked info box to prevent overlap
                            if (!string.IsNullOrEmpty(shortInitialInfoBoxId))
                            {
                                RemoveDrawObject(shortInitialInfoBoxId);
                                
                                if (ShowDebugInfo)
                                {
                                    Print($"🗑️ {Time[0]:HH:mm:ss} SHORT Removed initial blocked info box: {shortInitialInfoBoxId}");
                                }
                            }
                            
                            if (ShowDebugInfo)
                            {
                                Print($"✅ {Time[0]:HH:mm:ss} SHORT Delayed entry SUCCESS after {shortDelayedEntryBars} bars!");
                            }
                            
                            GenerateShortSignal(true, shortDelayedEntryBars); // Delayed entry
                        }
                        else if (ShowDebugInfo && barsSinceRetryStart == 1)
                        {
                            Print($"⏳ {Time[0]:HH:mm:ss} SHORT Retry attempt {barsSinceRetryStart}/{RetryBars} - filters still failing");
                        }
                    }
                    else
                    {
                        // Retry period expired
                        shortRetryActive = false;
                        
                        if (ShowDebugInfo)
                        {
                            Print($"⏰ {Time[0]:HH:mm:ss} SHORT Retry period expired ({RetryBars} bars) - no delayed entry");
                        }
                        
                        // Trade details will be shown in consolidated box at trigger bar
                    }
                }
            }
            
            // Reset conditions if we move too far from setup
            if (isInShortPullbackPhase && currentHigh < currentEMA34Low - (currentEMA34Low * 0.02)) // 2% below EMA
            {
                ResetShortEntryState();
                if (ShowDebugInfo)
                {
                    Print($"🔄 {Time[0]:HH:mm:ss} SHORT Reset: Moved too far below EMA ({currentHigh:F2} < {currentEMA34Low:F2})");
                }
            }
        }

        private bool CheckM3MomentumFilters(bool isLongTrade)
        {
            // Use built-in NT8 indicators for accurate intrabar calculations
        double currentMacd = phantomM3MacdLine;
        double previousMacd = GetPreviousM3MacdLine();
        double currentStoch = phantomM3StochK;
        double previousStoch = GetPreviousM3StochK();
    
        // DEBUG: Show M3 MACD and Stochastic values being received
        if (ShowDebugInfo)
        {
        Print($"🔍 M3 INDICATORS DEBUG - Bar Time: {Times[0][0]:HH:mm:ss.fff}");
        Print($"   Current M3 MACD: {currentMacd:F4}");
        Print($"   Previous M3 MACD: {previousMacd:F4}");
        Print($"   Current M3 Stoch K[0]: {currentStoch:F2}");
        Print($"   Previous M3 Stoch K[1]: {previousStoch:F2}");
        Print($"   M3 Bar Count: {CurrentBars[1]}");
        Print($"   M1 Bar Count: {CurrentBars[0]}");
        
        // DEBUG: Show M3 OHLC array data to verify phantom bar
        if (CurrentBars[1] >= 0)
        {
            Print($"🔍 M3 OHLC DEBUG - Current M3 Bar [0]:");
            Print($"   Open: {Opens[1][0]:F2}");
            Print($"   High: {Highs[1][0]:F2}");
            Print($"   Low: {Lows[1][0]:F2}");
            Print($"   Close: {Closes[1][0]:F2}");
            Print($"   Time: {Times[1][0]:HH:mm:ss}");
            
            if (CurrentBars[1] >= 1)
            {
                Print($"🔍 M3 OHLC DEBUG - Previous M3 Bar [1]:");
                Print($"   Open: {Opens[1][1]:F2}");
                Print($"   High: {Highs[1][1]:F2}");
                Print($"   Low: {Lows[1][1]:F2}");
                Print($"   Close: {Closes[1][1]:F2}");
                Print($"   Time: {Times[1][1]:HH:mm:ss}");
            }
        }
        }

        // Simple slope calculation: current > previous = UP, current < previous = DOWN
        bool macdAngleOK = isLongTrade ? currentMacd > previousMacd : currentMacd < previousMacd;
        bool stochAngleOK = isLongTrade ? currentStoch > previousStoch : currentStoch < previousStoch;

        // Waive Stochastic requirement if extreme values (from original logic)
        if (currentStoch > 80 || currentStoch < 20)
        {
        stochAngleOK = true;
        }

        bool result = macdAngleOK && stochAngleOK;

        if (ShowDebugInfo)
        {
        string tradeDirection = isLongTrade ? "LONG" : "SHORT";
        Print($"🔍 M3 MOMENTUM FILTERS ({tradeDirection}) - BUILT-IN NT8 INDICATORS:");
        Print($"   MACD: {currentMacd:F4} (current) vs {previousMacd:F4} (previous) = {(macdAngleOK ? "✅ PASS" : "❌ FAIL")}");
        Print($"   Stoch: {currentStoch:F1} (current) vs {previousStoch:F1} (previous) = {(stochAngleOK ? "✅ PASS" : "❌ FAIL")}");
        Print($"   M3 Result: {(result ? "✅ PASS (BOTH required)" : "❌ FAIL (BOTH required)")}");
        }

            return result;
        }

        private bool CheckM9MomentumFilters(bool isLongTrade)
        {
            // Use built-in NT8 indicators for M9 (same as IntrabarMTF)
        double currentMacd = phantomM9MacdLine;
        double previousMacd = GetPreviousM9MacdLine();
        double currentStoch = phantomM9StochK;
        double previousStoch = GetPreviousM9StochK();
    
        // DEBUG: Show M9 MACD and Stochastic values being received
        if (ShowDebugInfo)
        {
        Print($"🔍 M9 INDICATORS DEBUG - Bar Time: {Times[0][0]:HH:mm:ss.fff}");
        Print($"   Current M9 MACD: {currentMacd:F4}");
        Print($"   Previous M9 MACD: {previousMacd:F4}");
        Print($"   Current M9 Stoch K[0]: {currentStoch:F2}");
        Print($"   Previous M9 Stoch K[1]: {previousStoch:F2}");
        Print($"   M9 Bar Count: {CurrentBars[2]}");
        Print($"   M1 Bar Count: {CurrentBars[0]}");
        
        // DEBUG: Show M9 OHLC array data to verify phantom bar
        if (CurrentBars[2] >= 0)
        {
            Print($"🔍 M9 OHLC DEBUG - Current M9 Bar [0]:");
            Print($"   Open: {Opens[2][0]:F2}");
            Print($"   High: {Highs[2][0]:F2}");
            Print($"   Low: {Lows[2][0]:F2}");
            Print($"   Close: {Closes[2][0]:F2}");
            Print($"   Time: {Times[2][0]:HH:mm:ss}");
            
            if (CurrentBars[2] >= 1)
            {
                Print($"🔍 M9 OHLC DEBUG - Previous M9 Bar [1]:");
                Print($"   Open: {Opens[2][1]:F2}");
                Print($"   High: {Highs[2][1]:F2}");
                Print($"   Low: {Lows[2][1]:F2}");
                Print($"   Close: {Closes[2][1]:F2}");
                Print($"   Time: {Times[2][1]:HH:mm:ss}");
            }
        }
        }

        // Simple slope calculation: current > previous = UP, current < previous = DOWN
        bool macdAngleOK = isLongTrade ? currentMacd > previousMacd : currentMacd < previousMacd;
        bool stochAngleOK = isLongTrade ? currentStoch > previousStoch : currentStoch < previousStoch;

        // Waive Stochastic requirement if extreme values (from original logic)
        if (currentStoch > 80 || currentStoch < 20)
        {
        stochAngleOK = true;
        }

        bool result = macdAngleOK || stochAngleOK; // M9 uses OR logic (EITHER condition)

        if (ShowDebugInfo)
        {
        string tradeDirection = isLongTrade ? "LONG" : "SHORT";
        Print($"🔍 M9 MOMENTUM FILTERS ({tradeDirection}) - BUILT-IN NT8 INDICATORS:");
        Print($"   MACD: {currentMacd:F4} (current) vs {previousMacd:F4} (previous) = {(macdAngleOK ? "✅ PASS" : "❌ FAIL")}");
        Print($"   Stoch: {currentStoch:F1} (current) vs {previousStoch:F1} (previous) = {(stochAngleOK ? "✅ PASS" : "❌ FAIL")}");
        Print($"   M9 Result: {(result ? "✅ PASS (EITHER required)" : "❌ FAIL (EITHER required)")}");
        }

        return result;
        }

        // Angle checking methods - updated to work with file-based data
        private bool CheckMACDAngle(string timeframe, bool isLongTrade)
        {
            try
            {
                // Get MACD values from built-in indicators (no external files)
                double currentMACD, previousMACD, prevPrevMACD;
                
                if (timeframe == "M3")
                {
                    currentMACD = phantomM3MacdLine;
                    previousMACD = GetPreviousM3MacdLine();
                    prevPrevMACD = GetPrevPreviousM3MacdLine();
                }
                else // M9
                {
                    currentMACD = phantomM9MacdLine;
                    previousMACD = GetPreviousM9MacdLine();
                    prevPrevMACD = GetPrevPreviousM9MacdLine();
                }
            
            double macdSlope = currentMACD - previousMACD;
            
            bool angleMatches = isLongTrade ? (macdSlope > 0) : (macdSlope < 0);
            
            if (angleMatches)
            {
                if (ShowDebugInfo)
                {
                    string direction = isLongTrade ? "UP" : "DOWN";
                    string actualDirection = macdSlope > 0 ? "UP" : "DOWN";
                    
                    Print($"📊 {timeframe} MACD Details (FILE-BASED):");
                    Print($"   Current MACD: {currentMACD:F4}");
                    Print($"   Previous MACD: {previousMACD:F4}");
                    Print($"   Slope: {macdSlope:F4} ({actualDirection})");
                    Print($"   Required: {direction}, Got: {actualDirection}");
                    Print($"   Result: ✅ PASS (Angle matches trade direction)");
                }
                return true;
            }
            
            // If angle is opposite, check if momentum is WEAKENING (same wrong direction but less steep)
            double previousSlope = previousMACD - prevPrevMACD;
            
            // Check if momentum is weakening (same wrong direction but less steep)
            bool momentumWeakening = false;
            
            if (isLongTrade) // Want UP momentum, but current is DOWN
            {
                // Only allow if both current and previous are DOWN, and current is less steep (moving toward zero)
                if (macdSlope < 0 && previousSlope < 0)
                    momentumWeakening = macdSlope > previousSlope; // Less negative = less steep down = weakening
            }
            else // Want DOWN momentum, but current is UP
            {
                // Only allow if both current and previous are UP, and current is less steep (moving toward zero)  
                if (macdSlope > 0 && previousSlope > 0)
                    momentumWeakening = macdSlope < previousSlope; // Less positive = less steep up = weakening
            }
            
            if (ShowDebugInfo)
            {
                string direction = isLongTrade ? "UP" : "DOWN";
                string actualDirection = macdSlope > 0 ? "UP" : "DOWN";
                string prevDirection = previousSlope > 0 ? "UP" : "DOWN";
                string result = momentumWeakening ? "✅ PASS" : "❌ FAIL";
                
                Print($"📊 {timeframe} MACD Details (OPPOSING ANGLE - FILE-BASED):");
                Print($"   Current MACD: {currentMACD:F4}");
                Print($"   Previous MACD: {previousMACD:F4}");
                Print($"   Prev-Prev MACD: {prevPrevMACD:F4}");
                Print($"   Current Slope: {macdSlope:F4} ({actualDirection})");
                Print($"   Previous Slope: {previousSlope:F4} ({prevDirection})");
                Print($"   Required: {direction}, Got: {actualDirection}");
                
                if (momentumWeakening)
                {
                    Print($"   Momentum Weakening: ✅ YES (same wrong direction but less steep)");
                    Print($"   Explanation: Wrong direction momentum is reducing - acceptable");
                }
                else
                {
                    Print($"   Momentum Weakening: ❌ NO (momentum strengthening or reversing)");
                    Print($"   Explanation: Wrong direction momentum strengthening or direction reversing - not acceptable");
                }
                
                Print($"   Result: {result} (Momentum weakening check)");
            }
            
            return momentumWeakening;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"⚠️ Error in CheckMACDAngle: {ex.Message}");
                return false;
            }
        }
        
        private bool CheckStochAngle(string timeframe, bool isLongTrade)
        {
            try
            {
                double currentK = 0;
                double previousK = 0;
                
                // Get Stochastic values from phantom calculations
                if (timeframe == "M3")
                {
                    currentK = phantomM3StochK;
                    previousK = GetPreviousM3StochK();
                }
                else if (timeframe == "M9")
                {
                    currentK = phantomM9StochK;
                    previousK = GetPreviousM9StochK();
                }
                else
                {
                    return false; // Unknown timeframe
                }
            
                double kSlope = currentK - previousK;
            
            // Check if %K is in extreme zones (waive angle requirement)
            if (currentK > 80 || currentK < 20)
            {
                if (ShowDebugInfo)
                {
                    string zone = currentK > 80 ? "OVERBOUGHT (>80)" : "OVERSOLD (<20)";
                    string valueType = " (FILE-BASED)";
                    Print($"📊 {timeframe} Stoch %K Details{valueType}:");
                    Print($"   Current %K: {currentK:F1}");
                    Print($"   Zone: {zone}");
                    Print($"   Result: ✅ PASS (Extreme zone - angle requirement waived)");
                }
                return true;
            }
            
            // Check if angle matches trade direction
            bool angleMatches = isLongTrade ? (kSlope > 0) : (kSlope < 0);
            
            if (angleMatches)
            {
                if (ShowDebugInfo)
                {
                    string direction = isLongTrade ? "UP" : "DOWN";
                    string actualDirection = kSlope > 0 ? "UP" : "DOWN";
                    string valueType = " (FILE-BASED)";
                    
                    Print($"📊 {timeframe} Stoch %K Details{valueType}:");
                    Print($"   Current %K: {currentK:F1}");
                    Print($"   Previous %K: {previousK:F1}");
                    Print($"   Slope: {kSlope:F1} ({actualDirection})");
                    Print($"   Required: {direction}, Got: {actualDirection}");
                    Print($"   Result: ✅ PASS (Angle matches trade direction)");
                }
                return true;
            }
            
            // If angle is opposite, check if momentum is WEAKENING (same wrong direction but less steep)
            // Get previous-previous %K from built-in indicator
            double prevPrevK = 0;
            if (timeframe == "M3")
            {
                prevPrevK = GetPrevPreviousM3StochK();
            }
            else if (timeframe == "M9")
            {
                prevPrevK = GetPrevPreviousM9StochK();
            }
            else
            {
                return false; // Unknown timeframe
            }
            double previousSlope = previousK - prevPrevK;
            
            // Check if momentum is weakening (same wrong direction but less steep)
            bool momentumWeakening = false;
            
            if (isLongTrade) // Want UP momentum, but current is DOWN
            {
                // Only allow if both current and previous are DOWN, and current is less steep (moving toward zero)
                if (kSlope < 0 && previousSlope < 0)
                    momentumWeakening = kSlope > previousSlope; // Less negative = less steep down = weakening
            }
            else // Want DOWN momentum, but current is UP
            {
                // Only allow if both current and previous are UP, and current is less steep (moving toward zero)  
                if (kSlope > 0 && previousSlope > 0)
                    momentumWeakening = kSlope < previousSlope; // Less positive = less steep up = weakening
            }
            
            if (ShowDebugInfo)
            {
                string direction = isLongTrade ? "UP" : "DOWN";
                string actualDirection = kSlope > 0 ? "UP" : "DOWN";
                string prevDirection = previousSlope > 0 ? "UP" : "DOWN";
                string result = momentumWeakening ? "✅ PASS" : "❌ FAIL";
                string valueType = " (FILE-BASED)";
                
                Print($"📊 {timeframe} Stoch %K Details (OPPOSING ANGLE){valueType}:");
                Print($"   Current %K: {currentK:F1}");
                Print($"   Previous %K: {previousK:F1}");
                Print($"   Prev-Prev %K: {prevPrevK:F1}");
                Print($"   Current Slope: {kSlope:F1} ({actualDirection})");
                Print($"   Previous Slope: {previousSlope:F1} ({prevDirection})");
                Print($"   Required: {direction}, Got: {actualDirection}");
                
                if (momentumWeakening)
                {
                    Print($"   Momentum Weakening: ✅ YES (same wrong direction but less steep)");
                    Print($"   Explanation: Wrong direction momentum is reducing - acceptable");
                }
                else
                {
                    Print($"   Momentum Weakening: ❌ NO (momentum strengthening or reversing)");
                    Print($"   Explanation: Wrong direction momentum strengthening or direction reversing - not acceptable");
                }
                
                Print($"   Result: {result} (Momentum weakening check)");
            }
            
            return momentumWeakening;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"⚠️ Error in CheckStochAngle: {ex.Message}");
                return false;
            }
        }
        
        
        
        
        
        
        
        
        
        private void GenerateLongSignal(bool isDelayed = false, int delayBars = 0)
        {
            string signalType = isDelayed ? $"DELAYED ({delayBars} bars)" : "IMMEDIATE";
            
            if (ShowDebugInfo)
            {
                Print($"🚀 LONG SIGNAL GENERATED ({signalType}) at {Time[0]:HH:mm:ss}");
                Print($"   Close: {Close[0]:F2}, SMA5: {sma5Typical[0]:F2}");
                Print($"   All momentum filters passed!");
            }
            
            // Calculate stop loss and position sizing
            double entryPrice = Close[0];
            double stopLoss = CalculateStopLossPrice(true); // true for long
            
            if (stopLoss > 0 && stopLoss < entryPrice)
            {
                CalculateLongPositionSizing(entryPrice, stopLoss);
                
                if (ShowDebugInfo)
                {
                    Print($"💰 Position sizing completed:");
                    Print($"   Entry: {Instrument.MasterInstrument.FormatPrice(entryPrice)}");
                    Print($"   Stop Loss: {Instrument.MasterInstrument.FormatPrice(stopLoss)}");
                    Print($"   Position Size: {calculatedPositionSize:F0} {GetPositionSizeUnit()}");
                    Print($"   Risk Amount: ${riskAmount:F2}");
                    Print($"   Target: {Instrument.MasterInstrument.FormatPrice(targetPrice)}");
                }
                
                // EMA34SR validation - check if trade meets S/R criteria
                bool srApproved = ValidateTradeWithSRFiltering(true, entryPrice, stopLoss, targetPrice);
                
                if (!srApproved)
                {
                    if (ShowDebugInfo)
                        Print($"🚫 LONG signal blocked by EMA34SR filtering");
                    
                    // Rejection will be shown in consolidated trade details box
                    
                    return; // Exit without generating signal
                }
                
                // EMA Buffer validation - check if entry is in no-trade buffer zone
                string emaBufferBlockingReason;
                if (IsEntryBlockedByEMABuffers(entryPrice, true, out emaBufferBlockingReason))
                {
                    if (ShowDebugInfo)
                        Print($"🚫 LONG signal blocked by EMA buffer: {emaBufferBlockingReason}");
                    
                    // Rejection will be shown in consolidated trade details box
                    
                    return; // Exit without generating signal
                }
            }
            else
            {
                if (ShowDebugInfo)
                    Print($"⚠️ Invalid stop loss calculation: {Instrument.MasterInstrument.FormatPrice(stopLoss)} (entry: {Instrument.MasterInstrument.FormatPrice(entryPrice)})");
                return; // Exit if stop loss calculation failed
            }
            
            // CRITICAL: Always create info box for successful signals (showing "SIGNAL TAKEN")
            if (ShowInfoBoxes)
            {
                if (ShowDebugInfo)
                {
                    Print($"📦 CREATING SUCCESS INFO BOX for {signalType} signal at {Time[0]:HH:mm:ss}");
                }
                
                bool m3FiltersPass = currentBounceM3Pass;
                bool m9FiltersPass = currentBounceM9Pass;
                CreateBounceInfoBox(true, m3FiltersPass, m9FiltersPass, 0, Low[0], High[0], isDelayed, delayBars);
            }
            
            // Trade details already shown in consolidated box at trigger bar
            
            // Draw signal on chart with enhanced text including position info
            string signalText = isDelayed ? $"LONG +{delayBars}" : "LONG";
            if (calculatedPositionSize > 0 && ShowPositionSizing)
            {
                signalText += $"\n{calculatedPositionSize:F0} {GetPositionSizeUnit()}";
                signalText += $"\nSL: {Instrument.MasterInstrument.FormatPrice(stopLossPrice)}";
                signalText += $"\nTgt: {Instrument.MasterInstrument.FormatPrice(targetPrice)}";
            }
            Brush signalColor = isDelayed ? Brushes.DarkGreen : Brushes.Green;
            
            Draw.ArrowUp(this, $"LongSignal_{CurrentBar}", false, 0, Low[0] - (2 * TickSize), signalColor);
            // Draw.Text(this, $"LongText_{CurrentBar}", signalText, 0, Low[0] - (4 * TickSize), signalColor); // Commented out - using consolidated trade details box instead

            // STRATEGY: Enter long trade with calculated stop loss and target
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                // Calculate stop loss and profit target in ticks (entryPrice already declared above)
                int stopLossTicks = (int)Math.Round(Math.Abs(entryPrice - stopLossPrice) / TickSize);
                int profitTargetTicks = (int)Math.Round(Math.Abs(targetPrice - entryPrice) / TickSize);

                // Set stop loss and profit target
                SetStopLoss(CalculationMode.Ticks, stopLossTicks);
                SetProfitTarget(CalculationMode.Ticks, profitTargetTicks);

                // Enter long position
                EnterLong((int)calculatedPositionSize, "TrendContinuationLong");

                if (ShowDebugInfo)
                {
                    Print($"📈 STRATEGY: Entered LONG trade");
                    Print($"   Quantity: {calculatedPositionSize:F0}");
                    Print($"   Stop Loss: {stopLossTicks} ticks");
                    Print($"   Profit Target: {profitTargetTicks} ticks");
                }
            }

            // Reset for next signal
            ResetLongEntryState();
        }
        
        private void GenerateShortSignal(bool isDelayed = false, int delayBars = 0)
        {
            string signalType = isDelayed ? $"DELAYED ({delayBars} bars)" : "IMMEDIATE";
            
            if (ShowDebugInfo)
            {
                Print($"🔴 SHORT SIGNAL GENERATED ({signalType}) at {Time[0]:HH:mm:ss}");
                Print($"   Close: {Close[0]:F2}, SMA5: {sma5Typical[0]:F2}");
                Print($"   All momentum filters passed!");
            }
            
            // Calculate stop loss and position sizing
            double entryPrice = Close[0];
            double stopLoss = CalculateStopLossPrice(false); // false for short
            
            if (stopLoss > 0 && stopLoss > entryPrice)
            {
                CalculateShortPositionSizing(entryPrice, stopLoss);
                
                if (ShowDebugInfo)
                {
                    Print($"💰 Position sizing completed:");
                    Print($"   Entry: {Instrument.MasterInstrument.FormatPrice(entryPrice)}");
                    Print($"   Stop Loss: {Instrument.MasterInstrument.FormatPrice(stopLoss)}");
                    Print($"   Position Size: {calculatedPositionSize:F0} {GetPositionSizeUnit()}");
                    Print($"   Risk Amount: ${riskAmount:F2}");
                    Print($"   Target: {Instrument.MasterInstrument.FormatPrice(targetPrice)}");
                }
                
                // EMA34SR validation - check if trade meets S/R criteria
                bool srApproved = ValidateTradeWithSRFiltering(false, entryPrice, stopLoss, targetPrice);
                
                if (!srApproved)
                {
                    if (ShowDebugInfo)
                        Print($"🚫 SHORT signal blocked by EMA34SR filtering");
                    
                    // Rejection will be shown in consolidated trade details box
                    
                    return; // Exit without generating signal
                }
                
                // EMA Buffer validation - check if entry is in no-trade buffer zone
                string emaBufferBlockingReason;
                if (IsEntryBlockedByEMABuffers(entryPrice, false, out emaBufferBlockingReason))
                {
                    if (ShowDebugInfo)
                        Print($"🚫 SHORT signal blocked by EMA buffer: {emaBufferBlockingReason}");
                    
                    // Rejection will be shown in consolidated trade details box
                    
                    return; // Exit without generating signal
                }
            }
            else
            {
                if (ShowDebugInfo)
                    Print($"⚠️ Invalid stop loss calculation: {Instrument.MasterInstrument.FormatPrice(stopLoss)} (entry: {Instrument.MasterInstrument.FormatPrice(entryPrice)})");
                return; // Exit if stop loss calculation failed
            }
            
            // CRITICAL: Always create info box for successful signals (showing "SIGNAL TAKEN")
            if (ShowInfoBoxes)
            {
                if (ShowDebugInfo)
                {
                    Print($"📦 CREATING SUCCESS INFO BOX for {signalType} signal at {Time[0]:HH:mm:ss}");
                }
                
                bool m3FiltersPass = currentBounceM3Pass;
                bool m9FiltersPass = currentBounceM9Pass;
                CreateBounceInfoBox(false, m3FiltersPass, m9FiltersPass, 0, Low[0], High[0], isDelayed, delayBars);
            }
            
            // Trade details already shown in consolidated box at trigger bar
            
            // Draw signal on chart with enhanced text including position info
            string signalText = isDelayed ? $"SHORT +{delayBars}" : "SHORT";
            if (calculatedPositionSize > 0 && ShowPositionSizing)
            {
                signalText += $"\n{calculatedPositionSize:F0} {GetPositionSizeUnit()}";
                signalText += $"\nSL: {Instrument.MasterInstrument.FormatPrice(stopLossPrice)}";
                signalText += $"\nTgt: {Instrument.MasterInstrument.FormatPrice(targetPrice)}";
            }
            Brush signalColor = isDelayed ? Brushes.DarkRed : Brushes.Red;
            
            Draw.ArrowDown(this, $"ShortSignal_{CurrentBar}", false, 0, High[0] + (2 * TickSize), signalColor);
            // Draw.Text(this, $"ShortText_{CurrentBar}", signalText, 0, High[0] + (4 * TickSize), signalColor); // Commented out - using consolidated trade details box instead

            // STRATEGY: Enter short trade with calculated stop loss and target
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                // Calculate stop loss and profit target in ticks (entryPrice already declared above)
                int stopLossTicks = (int)Math.Round(Math.Abs(stopLossPrice - entryPrice) / TickSize);
                int profitTargetTicks = (int)Math.Round(Math.Abs(entryPrice - targetPrice) / TickSize);

                // Set stop loss and profit target
                SetStopLoss(CalculationMode.Ticks, stopLossTicks);
                SetProfitTarget(CalculationMode.Ticks, profitTargetTicks);

                // Enter short position
                EnterShort((int)calculatedPositionSize, "TrendContinuationShort");

                if (ShowDebugInfo)
                {
                    Print($"📉 STRATEGY: Entered SHORT trade");
                    Print($"   Quantity: {calculatedPositionSize:F0}");
                    Print($"   Stop Loss: {stopLossTicks} ticks");
                    Print($"   Profit Target: {profitTargetTicks} ticks");
                }
            }

            // Reset for next signal
            ResetShortEntryState();
        }
        
        private void ResetLongEntryState()
        {
            isInLongPullbackPhase = false;
            hasEMAHighTouched = false;
            longPullbackConfirmed = false;
            barsAboveEMAHigh = 0;
            longPullbackHighWaterMark = 0;
            longPullbackStartTime = DateTime.MinValue; // Reset pullback start time
            longInfoBoxCreated = false;
            longTriggerBarIndex = 0;
            longTriggerBarLow = 0;
            longTriggerBarHigh = 0;
            longClosesAgainstCount = 0;
            longHighestBarIndex = 0;
            
            // Reset extreme tracking
            longExtremeTime = DateTime.MinValue;
            longExtremePrice = 0;
            longTrackingExtreme = false;
            
            // Reset retry tracking
            longRetryActive = false;
            longRetryStartBar = -1;
            longDelayedEntryBars = 0;
            longInitialInfoBoxId = ""; // Clear info box ID
            longTriggerAttempted = false; // Clear trigger attempt flag
            
            // Clear bounce validation data
            ClearBounceValidationData();
        }
        
        private void ResetShortEntryState()
        {
            isInShortPullbackPhase = false;
            hasEMALowTouched = false;
            shortPullbackConfirmed = false;
            barsBelowEMALow = 0;
            shortPullbackLowWaterMark = 0;
            shortPullbackStartTime = DateTime.MinValue; // Reset pullback start time
            shortInfoBoxCreated = false;
            shortTriggerBarIndex = 0;
            shortTriggerBarLow = 0;
            shortTriggerBarHigh = 0;
            shortClosesAgainstCount = 0;
            shortLowestBarIndex = 0;
            
            // Reset extreme tracking
            shortExtremeTime = DateTime.MinValue;
            shortExtremePrice = 0;
            shortTrackingExtreme = false;
            
            // Reset retry tracking
            shortRetryActive = false;
            shortRetryStartBar = -1;
            shortDelayedEntryBars = 0;
            shortInitialInfoBoxId = ""; // Clear info box ID
            shortTriggerAttempted = false; // Clear trigger attempt flag
            
            // Clear bounce validation data
            ClearBounceValidationData();
        }
        
        private string CreateBounceInfoBox(bool isLongTrade, bool m3FiltersPass, bool m9FiltersPass, int barsAgo, double triggerLow, double triggerHigh, bool isDelayed = false, int delayBars = 0)
        {
            bounceCounter++;
            string boxId = $"BounceInfo_{CurrentBar}_{bounceCounter}";
            
            // Get detailed filter status using captured trading decision values
            var m3Details = GetM3FilterDetails(isLongTrade);
            var m9Details = GetM9FilterDetails(isLongTrade);
            
            // Build info text
            string direction = isLongTrade ? "LONG" : "SHORT";
            string triggerText = isLongTrade ? "Close > 5MA" : "Close < 5MA";
            
            StringBuilder infoText = new StringBuilder();
            infoText.AppendLine($"{direction} BOUNCE");
            infoText.AppendLine($"Time: {Time[barsAgo]:HH:mm:ss}");
            infoText.AppendLine($"{triggerText}: ✓");
            infoText.AppendLine("");
            
            // M3 filter details
            string m3Status = m3FiltersPass ? "✓ PASS" : "❌ FAIL";
            infoText.AppendLine($"M3 Filters: {m3Status}");
            infoText.AppendLine($"  MACD: {m3Details.MacdStatus}");
            infoText.AppendLine($"  %K: {m3Details.StochStatus}");
            infoText.AppendLine("");
            
            // M9 filter details  
            string m9Status = m9FiltersPass ? "✓ PASS" : "❌ FAIL";
            infoText.AppendLine($"M9 Filters: {m9Status}");
            infoText.AppendLine($"  MACD: {m9Details.MacdStatus}");
            infoText.AppendLine($"  %K: {m9Details.StochStatus}");
            infoText.AppendLine("");
            
            // Final result with delayed entry information
            bool signalTaken = m3FiltersPass && m9FiltersPass;
            string result;
            
            if (signalTaken)
            {
                if (isDelayed && delayBars > 0)
                {
                    result = $"✓ SIGNAL TAKEN (Delayed +{delayBars} bars)";
                }
                else
                {
                    result = "✓ SIGNAL TAKEN (Immediate)";
                }
            }
            else
            {
                if (RetryBars > 0)
                {
                    result = $"❌ SIGNAL BLOCKED (Retry: {RetryBars} bars)";
                }
                else
                {
                    result = "❌ SIGNAL BLOCKED (No retry)";
                }
            }
            
            infoText.AppendLine($"Result: {result}");
            
            // Choose colors based on result
            Brush textColor = signalTaken ? Brushes.Black : Brushes.Black;
            Brush bgColor = signalTaken ? Brushes.LightGreen : Brushes.LightPink;
            
            // Position info box at trigger bar location
            double yOffset = isLongTrade ? triggerLow - (6 * TickSize) : triggerHigh + (6 * TickSize);
            
            // Draw info box positioned at the trigger bar with smaller font
            Draw.Text(this, boxId, false, infoText.ToString(), barsAgo, yOffset, 0, textColor, 
                     new Gui.Tools.SimpleFont("Arial", 11), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            
            
            return boxId; // Return the box ID for potential deletion
        }
        
        // NEW: Comprehensive rejection info box with entry/SL/TP details
        private string CreateRejectionInfoBox(bool isLongTrade, string rejectionReason, double entryPrice, double stopLoss = 0, double target = 0, double positionSize = 0)
        {
            if (!ShowTradeDetails)
                return "";
                
            bounceCounter++;
            string boxId = $"RejectionInfo_{CurrentBar}_{bounceCounter}";
            
            string direction = isLongTrade ? "LONG" : "SHORT";
            string triggerText = isLongTrade ? "Close > 5MA" : "Close < 5MA";
            
            StringBuilder infoText = new StringBuilder();
            infoText.AppendLine($"🚫 {direction} REJECTED");
            infoText.AppendLine($"Time: {Time[0]:HH:mm:ss}");
            infoText.AppendLine($"{triggerText}: ✓");
            infoText.AppendLine($"❌ REASON: {rejectionReason}");
            infoText.AppendLine("");
            
            // Trade details
            infoText.AppendLine($"📊 Entry: {Instrument.MasterInstrument.FormatPrice(entryPrice)}");
            
            if (stopLoss > 0)
            {
                double riskPoints = Math.Abs(entryPrice - stopLoss);
                infoText.AppendLine($"Stop: {Instrument.MasterInstrument.FormatPrice(stopLoss)}");
                infoText.AppendLine($"Risk: {Instrument.MasterInstrument.FormatPrice(riskPoints)} pts");
            }
            
            if (target > 0)
            {
                double rewardPoints = Math.Abs(target - entryPrice);
                double rrRatio = (stopLoss > 0) ? rewardPoints / Math.Abs(entryPrice - stopLoss) : 0;
                infoText.AppendLine($"Target: {Instrument.MasterInstrument.FormatPrice(target)}");
                infoText.AppendLine($"Reward: {Instrument.MasterInstrument.FormatPrice(rewardPoints)} pts");
                if (rrRatio > 0)
                    infoText.AppendLine($"R/R: {rrRatio:F2}");
            }
            
            if (positionSize > 0)
            {
                string unit = GetPositionSizeUnit();
                infoText.AppendLine($"Size: {positionSize:F0} {unit}");
            }
            infoText.AppendLine("");
            
            // Simplified momentum status - just checkmarks
            try
            {
                bool m3FiltersPass = CheckM3MomentumFilters(isLongTrade);
                bool m9FiltersPass = CheckM9MomentumFilters(isLongTrade);
                
                string m3Status = m3FiltersPass ? "✓" : "❌";
                string m9Status = m9FiltersPass ? "✓" : "❌";
                
                infoText.AppendLine($"📈 M3: {m3Status}  M9: {m9Status}");
            }
            catch
            {
                // Skip momentum details if not available
            }
            
            // Position info box using same positioning as regular info boxes
            double triggerLow = Low[0];
            double triggerHigh = High[0];
            double yOffset = isLongTrade ? triggerLow - (6 * TickSize) : triggerHigh + (6 * TickSize);
            
            // Choose colors for rejection - purple text for trade details
            Brush textColor = Brushes.Purple;
            
            // Create the info box positioned at the trigger bar with same styling as regular info boxes
            Draw.Text(this, boxId, false, infoText.ToString(), 0, yOffset, 0, textColor, 
                     new Gui.Tools.SimpleFont("Arial", 11), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            
            return boxId;
        }
        
        // Helper method to build momentum rejection reason
        private string BuildMomentumRejectionReason(bool isLongTrade, bool m3FiltersPass, bool m9FiltersPass)
        {
            string direction = isLongTrade ? "up" : "down";
            
            if (!m3FiltersPass && !m9FiltersPass)
                return $"Both M3 and M9 momentum not trending {direction}";
            else if (!m3FiltersPass)
                return $"M3 momentum not trending {direction}";
            else if (!m9FiltersPass)
                return $"M9 momentum not trending {direction}";
            else
                return $"Momentum filters failed";
        }
        
        // NEW: Get detailed EMA34SR rejection information with timeframe and level details
        private string GetEMA34SRRejectionDetails(bool isLongTrade, double entryPrice, double stopLoss, double target)
        {
            try
            {
                if (!IsEMA34SREnabled())
                    return "EMA34SR disabled";
                
                // Collect EMA34SR levels to get detailed information
                CollectEMA34SRLevels(entryPrice);
                
                if (allSRLevels.Count == 0)
                    return "EMA34SR: No S/R levels found";
                
                // PATH-CROSSING LOGIC: Find levels that block the path from entry to target (same as ValidateTradeWithSRFiltering)
                SRLevel blockingLevel = null;
                double effectiveTarget = target;
                
                foreach (var level in allSRLevels)
                {
                    // Check if this level blocks the trade path from entry to target
                    bool blocksPath = false;
                    
                    if (isLongTrade)
                    {
                        // For LONG: Level blocks path if it's between entry and target
                        blocksPath = (level.Price > entryPrice && level.Price < target);
                    }
                    else
                    {
                        // For SHORT: Level blocks path if it's between entry and target  
                        blocksPath = (level.Price < entryPrice && level.Price > target);
                    }
                    
                    if (blocksPath)
                    {
                        // Find the closest blocking level (first one hit on path to target)
                        if (blockingLevel == null)
                        {
                            blockingLevel = level;
                            effectiveTarget = level.Price; // Can only reach this level, not full target
                        }
                        else
                        {
                            // Update to closer blocking level
                            bool isCloser = isLongTrade ? 
                                (level.Price < blockingLevel.Price) : // For LONG, lower blocking levels are closer
                                (level.Price > blockingLevel.Price);   // For SHORT, higher blocking levels are closer
                            
                            if (isCloser)
                            {
                                blockingLevel = level;
                                effectiveTarget = level.Price;
                            }
                        }
                    }
                }
                
                if (blockingLevel == null)
                    return "EMA34SR: No levels block path to target";
                
                // Calculate available profit up to the blocking level
                double riskPoints = Math.Abs(entryPrice - stopLoss);
                double availableReward = Math.Abs(effectiveTarget - entryPrice);
                double availableRR = riskPoints > 0 ? availableReward / riskPoints : 0;
                
                // Check if available profit to blocking level meets minimum requirements
                bool meetsSRMinRR = availableRR >= MinimumRiskReward;
                
                // Check ATR-based distance requirement (entry must not be too close to blocking level)
                double currentATR = atr[0];
                double requiredDistanceFromSR = (DistanceFromSRAsATRPercent / 100.0) * currentATR;
                double actualDistanceFromSR = Math.Abs(blockingLevel.Price - entryPrice);
                bool hasEnoughClearance = actualDistanceFromSR >= requiredDistanceFromSR;
                
                // Build detailed rejection reason - show actual level type and path-crossing context
                string pathContext = $"Entry {Instrument.MasterInstrument.FormatPrice(entryPrice)}→Target {Instrument.MasterInstrument.FormatPrice(target)}";
                string levelDescription = $"{blockingLevel.TimeFrame} {blockingLevel.Type} @ {Instrument.MasterInstrument.FormatPrice(blockingLevel.Price)} blocks {pathContext}";
                
                if (!meetsSRMinRR && !hasEnoughClearance)
                {
                    return $"EMA34SR: {levelDescription} - Available R/R too low ({availableRR:F2}<{MinimumRiskReward:F2}) & too close ({Instrument.MasterInstrument.FormatPrice(actualDistanceFromSR)} < {Instrument.MasterInstrument.FormatPrice(requiredDistanceFromSR)})";
                }
                else if (!meetsSRMinRR)
                {
                    return $"EMA34SR: {levelDescription} - Available R/R too low ({availableRR:F2} < {MinimumRiskReward:F2})";
                }
                else if (!hasEnoughClearance)
                {
                    return $"EMA34SR: {levelDescription} - too close ({Instrument.MasterInstrument.FormatPrice(actualDistanceFromSR)} < {Instrument.MasterInstrument.FormatPrice(requiredDistanceFromSR)})";
                }
                
                return $"EMA34SR: {levelDescription} - validation failed";
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error in GetEMA34SRRejectionDetails: {ex.Message}");
                return "EMA34SR: Error getting rejection details";
            }
        }
        
        // NEW: Get EMA34SR status details for trade details box (both approved and rejected)
        private string GetEMA34SRStatusDetails(bool isLongTrade, double entryPrice, double stopLoss, double target, bool isApproved)
        {
            try
            {
                if (!IsEMA34SREnabled())
                    return "";
                
                // Collect EMA34SR levels to get detailed information
                CollectEMA34SRLevels(entryPrice);
                
                if (allSRLevels.Count == 0)
                    return "(No S/R levels)";
                
                // PATH-CROSSING LOGIC: Find levels that block the path from entry to target
                SRLevel blockingLevel = null;
                double effectiveTarget = target;
                
                foreach (var level in allSRLevels)
                {
                    // Check if this level blocks the trade path from entry to target
                    bool blocksPath = false;
                    
                    if (isLongTrade)
                    {
                        // For LONG: Level blocks path if it's between entry and target
                        blocksPath = (level.Price > entryPrice && level.Price < target);
                    }
                    else
                    {
                        // For SHORT: Level blocks path if it's between entry and target  
                        blocksPath = (level.Price < entryPrice && level.Price > target);
                    }
                    
                    if (blocksPath)
                    {
                        // Find the closest blocking level (first one hit on path to target)
                        if (blockingLevel == null)
                        {
                            blockingLevel = level;
                            effectiveTarget = level.Price; // Can only reach this level, not full target
                        }
                        else
                        {
                            // Update to closer blocking level
                            bool isCloser = isLongTrade ? 
                                (level.Price < blockingLevel.Price) : // For LONG, lower blocking levels are closer
                                (level.Price > blockingLevel.Price);   // For SHORT, higher blocking levels are closer
                            
                            if (isCloser)
                            {
                                blockingLevel = level;
                                effectiveTarget = level.Price;
                            }
                        }
                    }
                }
                
                if (blockingLevel == null)
                {
                    return "(Clear path)";
                }
                
                // Calculate available profit up to the blocking level
                double riskPoints = Math.Abs(entryPrice - stopLoss);
                double availableReward = Math.Abs(effectiveTarget - entryPrice);
                double availableRR = riskPoints > 0 ? availableReward / riskPoints : 0;
                
                // Show blocking level info with path-crossing context
                string levelInfo = $"{blockingLevel.TimeFrame} {blockingLevel.Type} @ {Instrument.MasterInstrument.FormatPrice(blockingLevel.Price)}";
                
                if (isApproved)
                {
                    return $"(Blocked by {levelInfo}, R/R={availableRR:F2})";
                }
                else
                {
                    // For rejected trades, show the rejection reason
                    bool meetsSRMinRR = availableRR >= MinimumRiskReward;
                    double currentATR = atr[0];
                    double requiredDistanceFromSR = (DistanceFromSRAsATRPercent / 100.0) * currentATR;
                    double actualDistanceFromSR = Math.Abs(blockingLevel.Price - entryPrice);
                    bool hasEnoughClearance = actualDistanceFromSR >= requiredDistanceFromSR;
                    
                    if (!meetsSRMinRR && !hasEnoughClearance)
                    {
                        return $"(Blocked by {levelInfo}, R/R too low & too close)";
                    }
                    else if (!meetsSRMinRR)
                    {
                        return $"(Blocked by {levelInfo}, R/R too low {availableRR:F2}<{MinimumRiskReward:F2})";
                    }
                    else if (!hasEnoughClearance)
                    {
                        return $"(Blocked by {levelInfo}, too close)";
                    }
                    else
                    {
                        return $"(Blocked by {levelInfo}, unknown reason)";
                    }
                }
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error in GetEMA34SRStatusDetails: {ex.Message}");
                return "(Error)";
            }
        }
        
        // NEW: Get effective target considering EMA34SR path-crossing blocking
        private double GetEffectiveTarget(bool isLongTrade, double entryPrice, double originalTarget)
        {
            try
            {
                if (!IsEMA34SREnabled())
                    return originalTarget; // No EMA34SR filtering, use original target
                
                // Collect EMA34SR levels to check for blocking
                CollectEMA34SRLevels(entryPrice);
                
                if (allSRLevels.Count == 0)
                    return originalTarget; // No S/R levels, use original target
                
                // PATH-CROSSING LOGIC: Find the closest level that blocks the path from entry to original target
                SRLevel blockingLevel = null;
                
                foreach (var level in allSRLevels)
                {
                    // Check if this level blocks the trade path from entry to target
                    bool blocksPath = false;
                    
                    if (isLongTrade)
                    {
                        // For LONG: Level blocks path if it's between entry and target
                        blocksPath = (level.Price > entryPrice && level.Price < originalTarget);
                    }
                    else
                    {
                        // For SHORT: Level blocks path if it's between entry and target  
                        blocksPath = (level.Price < entryPrice && level.Price > originalTarget);
                    }
                    
                    if (blocksPath)
                    {
                        // Find the closest blocking level (first one hit on path to target)
                        if (blockingLevel == null)
                        {
                            blockingLevel = level;
                        }
                        else
                        {
                            // Update to closer blocking level
                            bool isCloser = isLongTrade ? 
                                (level.Price < blockingLevel.Price) : // For LONG, lower blocking levels are closer
                                (level.Price > blockingLevel.Price);   // For SHORT, higher blocking levels are closer
                            
                            if (isCloser)
                            {
                                blockingLevel = level;
                            }
                        }
                    }
                }
                
                // If there's a blocking level, the effective target is that level
                return blockingLevel != null ? blockingLevel.Price : originalTarget;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error in GetEffectiveTarget: {ex.Message}");
                return originalTarget; // On error, use original target
            }
        }
        
        // NEW: Get detailed EMA buffer rejection information with timeframe and zone details
        private string GetEMABufferRejectionDetails(bool isLongTrade, double entryPrice)
        {
            try
            {
                if (!IsEMABuffersEnabled())
                    return "EMA buffers disabled";
                
                // Check each timeframe to find which one is blocking
                foreach (var timeFrame in emaNoLongZoneStart.Keys)
                {
                    if (isLongTrade)
                    {
                        // Check if LONG entry is in no-long zone
                        double zoneStart = emaNoLongZoneStart[timeFrame];
                        double zoneEnd = emaNoLongZoneEnd[timeFrame];
                        
                        if (zoneStart > 0 && zoneEnd > 0)
                        {
                            bool inNoLongZone = entryPrice >= zoneStart && entryPrice <= zoneEnd;
                            
                            if (inNoLongZone)
                            {
                                return $"EMA Buffer: Entry {Instrument.MasterInstrument.FormatPrice(entryPrice)} in {timeFrame} no-LONG zone ({Instrument.MasterInstrument.FormatPrice(zoneStart)} to {Instrument.MasterInstrument.FormatPrice(zoneEnd)})";
                            }
                        }
                    }
                    else
                    {
                        // Check if SHORT entry is in no-short zone
                        double zoneStart = emaNoShortZoneStart[timeFrame];
                        double zoneEnd = emaNoShortZoneEnd[timeFrame];
                        
                        if (zoneStart > 0 && zoneEnd > 0)
                        {
                            bool inNoShortZone = entryPrice <= zoneStart && entryPrice >= zoneEnd;
                            
                            if (inNoShortZone)
                            {
                                return $"EMA Buffer: Entry {Instrument.MasterInstrument.FormatPrice(entryPrice)} in {timeFrame} no-SHORT zone ({Instrument.MasterInstrument.FormatPrice(zoneEnd)} to {Instrument.MasterInstrument.FormatPrice(zoneStart)})";
                            }
                        }
                    }
                }
                
                return "EMA Buffer: No specific buffer zone blocking found";
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error in GetEMABufferRejectionDetails: {ex.Message}");
                return "EMA Buffer: Error getting rejection details";
            }
        }
        
        // NEW: Enhanced success info box with complete trade details
        private string CreateSuccessInfoBox(bool isLongTrade, double entryPrice, double stopLoss, double target, double positionSize, bool isDelayed = false, int delayBars = 0)
        {
            if (!ShowTradeDetails)
                return "";
                
            bounceCounter++;
            string boxId = $"SuccessInfo_{CurrentBar}_{bounceCounter}";
            
            string direction = isLongTrade ? "LONG" : "SHORT";
            string signalType = isDelayed ? $"DELAYED +{delayBars}" : "IMMEDIATE";
            
            StringBuilder infoText = new StringBuilder();
            infoText.AppendLine($"✅ {direction} ACCEPTED");
            infoText.AppendLine($"Time: {Time[0]:HH:mm:ss}");
            infoText.AppendLine($"Type: {signalType}");
            infoText.AppendLine("");
            
            infoText.AppendLine($"📊 Entry: {Instrument.MasterInstrument.FormatPrice(entryPrice)}");
            
            if (stopLoss > 0)
            {
                double riskPoints = Math.Abs(entryPrice - stopLoss);
                infoText.AppendLine($"Stop: {Instrument.MasterInstrument.FormatPrice(stopLoss)}");
                infoText.AppendLine($"Risk: {Instrument.MasterInstrument.FormatPrice(riskPoints)} pts");
            }
            
            if (target > 0)
            {
                double rewardPoints = Math.Abs(target - entryPrice);
                double rrRatio = (stopLoss > 0) ? rewardPoints / Math.Abs(entryPrice - stopLoss) : 0;
                infoText.AppendLine($"Target: {Instrument.MasterInstrument.FormatPrice(target)}");
                infoText.AppendLine($"Reward: {Instrument.MasterInstrument.FormatPrice(rewardPoints)} pts");
                if (rrRatio > 0)
                    infoText.AppendLine($"R/R: {rrRatio:F2}");
            }
            
            if (positionSize > 0)
            {
                string unit = GetPositionSizeUnit();
                double riskAmount = (stopLoss > 0) ? Math.Abs(entryPrice - stopLoss) * positionSize : 0;
                infoText.AppendLine($"Size: {positionSize:F0} {unit}");
                if (riskAmount > 0)
                    infoText.AppendLine($"Risk $: ${riskAmount:F0}");
            }
            infoText.AppendLine("");
            
            // Simplified momentum confirmation - just checkmarks
            try
            {
                bool m3FiltersPass = CheckM3MomentumFilters(isLongTrade);
                bool m9FiltersPass = CheckM9MomentumFilters(isLongTrade);
                
                string m3Status = m3FiltersPass ? "✓" : "❌";
                string m9Status = m9FiltersPass ? "✓" : "❌";
                
                infoText.AppendLine($"📈 M3: {m3Status}  M9: {m9Status}");
            }
            catch
            {
                // Skip momentum details if not available
            }
            
            // Position info box using same positioning as regular info boxes
            double triggerLow = Low[0];
            double triggerHigh = High[0];
            double yOffset = isLongTrade ? triggerLow - (6 * TickSize) : triggerHigh + (6 * TickSize);
            
            // Choose colors for success - purple text for trade details
            Brush textColor = Brushes.Purple;
            
            // Create the info box positioned at the trigger bar with same styling as regular info boxes
            Draw.Text(this, boxId, false, infoText.ToString(), 0, yOffset, 0, textColor, 
                     new Gui.Tools.SimpleFont("Arial", 11), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            
            return boxId;
        }
        
        // NEW: Consolidated trade details box - shows all validation results in one box per bounce
        private string CreateConsolidatedTradeDetailsBox(bool isLongTrade, bool m3FiltersPass, bool m9FiltersPass, int delayBars = 0, 
                                                        double entryPrice = 0, double stopLoss = 0, double target = 0, double positionSize = 0, string result = "")
        {
            if (!ShowTradeDetails)
                return "";
                
            bounceCounter++;
            string boxId = $"ConsolidatedTradeDetails_{CurrentBar}_{bounceCounter}";
            
            string direction = isLongTrade ? "LONG" : "SHORT";
            string triggerText = isLongTrade ? "Close > 5MA" : "Close < 5MA";
            
            StringBuilder infoText = new StringBuilder();
            
            // If no explicit result provided, determine from momentum filters
            if (string.IsNullOrEmpty(result))
            {
                if (m3FiltersPass && m9FiltersPass)
                    result = "ACCEPTED";
                else
                    result = "REJECTED - Momentum";
            }
            
            // Header
            string resultIcon = result.StartsWith("ACCEPTED") ? "✅" : "🚫";
            infoText.AppendLine($"{resultIcon} {direction} {result}");
            infoText.AppendLine($"Time: {Time[0]:HH:mm:ss}");
            infoText.AppendLine($"{triggerText}: ✓");
            if (delayBars > 0)
                infoText.AppendLine($"Delay: +{delayBars} bars");
            infoText.AppendLine("");
            
            // Trade levels - calculate if not provided
            if (entryPrice == 0) entryPrice = Close[0];
            if (stopLoss == 0) stopLoss = CalculateStopLossPrice(isLongTrade);
            if (target == 0)
            {
                if (isLongTrade)
                    target = entryPrice + (RiskRewardRatio * Math.Abs(entryPrice - stopLoss));
                else
                    target = entryPrice - (RiskRewardRatio * Math.Abs(entryPrice - stopLoss));
            }
            if (positionSize == 0)
            {
                if (isLongTrade)
                {
                    CalculateLongPositionSizing(entryPrice, stopLoss);
                    positionSize = calculatedPositionSize;
                }
                else
                {
                    CalculateShortPositionSizing(entryPrice, stopLoss);
                    positionSize = calculatedPositionSize;
                }
            }
            
            // Trade details
            infoText.AppendLine($"📊 Entry: {Instrument.MasterInstrument.FormatPrice(entryPrice)}");
            if (stopLoss > 0)
            {
                double riskPoints = Math.Abs(entryPrice - stopLoss);
                infoText.AppendLine($"Stop: {Instrument.MasterInstrument.FormatPrice(stopLoss)}");
                infoText.AppendLine($"Risk: {Instrument.MasterInstrument.FormatPrice(riskPoints)} pts");
            }
            if (target > 0)
            {
                double rewardPoints = Math.Abs(target - entryPrice);
                double rrRatio = (stopLoss > 0) ? rewardPoints / Math.Abs(entryPrice - stopLoss) : 0;
                infoText.AppendLine($"Target: {Instrument.MasterInstrument.FormatPrice(target)}");
                infoText.AppendLine($"Reward: {Instrument.MasterInstrument.FormatPrice(rewardPoints)} pts");
                if (rrRatio > 0)
                    infoText.AppendLine($"R/R: {rrRatio:F2}");
            }
            if (positionSize > 0)
            {
                string unit = GetPositionSizeUnit();
                infoText.AppendLine($"Size: {positionSize:F0} {unit}");
            }
            infoText.AppendLine("");
            
            // Validation results
            string m3Status = m3FiltersPass ? "✓" : "❌";
            string m9Status = m9FiltersPass ? "✓" : "❌";
            infoText.AppendLine($"📈 M3: {m3Status}  M9: {m9Status}");
            
            // Additional validation results (EMA34SR, EMA Buffers) if applicable
            if (IsEMA34SREnabled() && entryPrice > 0 && stopLoss > 0 && target > 0)
            {
                bool srApproved = ValidateTradeWithSRFiltering(isLongTrade, entryPrice, stopLoss, target);
                string srStatus = srApproved ? "✓" : "❌";
                infoText.AppendLine($"EMA34SR: {srStatus}");
            }
            
            if (IsEMABuffersEnabled() && entryPrice > 0)
            {
                string emaBufferReason;
                bool bufferOK = !IsEntryBlockedByEMABuffers(entryPrice, isLongTrade, out emaBufferReason);
                string bufferStatus = bufferOK ? "✓" : "❌";
                infoText.AppendLine($"EMA Buffer: {bufferStatus}");
            }
            
            // Position info box using same positioning as regular info boxes
            double triggerLow = Low[0];
            double triggerHigh = High[0];
            double yOffset = isLongTrade ? triggerLow - (6 * TickSize) : triggerHigh + (6 * TickSize);
            
            // Purple text for trade details
            Brush textColor = Brushes.Purple;
            
            // Create the info box positioned at the trigger bar with same styling as regular info boxes
            Draw.Text(this, boxId, false, infoText.ToString(), 0, yOffset, 0, textColor, 
                     new Gui.Tools.SimpleFont("Arial", 11), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            
            return boxId;
        }

        // Create bounce criteria info box showing trend direction/count and MACD move numbers for all enabled timeframes
        private string CreateBounceCriteriaInfoBox(bool isLongTrade)
        {
            if (!ShowBounceCriteria)
                return "";

            var infoText = new System.Text.StringBuilder();
            infoText.AppendLine(isLongTrade ? "LONG BOUNCE CRITERIA:" : "SHORT BOUNCE CRITERIA:");
            infoText.AppendLine("");

            // Helper to get trend data for each timeframe
            void AddTimeframeData(string timeframeName, bool priceFilterEnabled, bool momentumFilterEnabled)
            {
                if (!priceFilterEnabled && !momentumFilterEnabled)
                    return; // Skip disabled timeframes

                try
                {
                    // Get price trend data
                    string priceDirection = "Unknown";
                    int priceCycles = 0;
                    if (priceFilterEnabled)
                    {
                        var smaState = GetSMADirectionState(timeframeName);
                        priceDirection = smaState.lastNonFlatDirection == SMADirection.Up ? "Up" :
                                        smaState.lastNonFlatDirection == SMADirection.Down ? "Down" : "Unknown";
                        priceCycles = smaState.cyclesSinceDirectionChange;
                    }

                    // Get MACD trend data
                    string macdDirection = "Unknown";
                    int macdMoves = 0;
                    if (momentumFilterEnabled)
                    {
                        var macdState = GetMACDTrendState(timeframeName);
                        macdDirection = macdState.currentTrend == MomentumTrend.Up ? "Up" :
                                       macdState.currentTrend == MomentumTrend.Down ? "Down" : "Unknown";
                        macdMoves = macdState.trendMovesSinceDirectionChange;
                    }

                    // Add line for this timeframe
                    if (priceFilterEnabled && momentumFilterEnabled)
                    {
                        infoText.AppendLine($"{timeframeName}: Price {priceDirection}#{priceCycles}, MACD {macdDirection}#{macdMoves}");
                    }
                    else if (priceFilterEnabled)
                    {
                        infoText.AppendLine($"{timeframeName}: Price {priceDirection}#{priceCycles}");
                    }
                    else if (momentumFilterEnabled)
                    {
                        infoText.AppendLine($"{timeframeName}: MACD {macdDirection}#{macdMoves}");
                    }
                }
                catch (Exception ex)
                {
                    infoText.AppendLine($"{timeframeName}: Error - {ex.Message}");
                }
            }

            // Add data for all enabled timeframes
            AddTimeframeData("M3", EnablePriceTrendFilter_M3, EnableMomentumTrendFilter_M3);
            AddTimeframeData("M9", EnablePriceTrendFilter_M9, EnableMomentumTrendFilter_M9);
            AddTimeframeData("M15", EnablePriceTrendFilter_M15, EnableMomentumTrendFilter_M15);
            AddTimeframeData("M30", EnablePriceTrendFilter_M30, EnableMomentumTrendFilter_M30);
            AddTimeframeData("M60", EnablePriceTrendFilter_M60, EnableMomentumTrendFilter_M60);
            AddTimeframeData("M240", EnablePriceTrendFilter_M240, EnableMomentumTrendFilter_M240);
            AddTimeframeData("Daily", EnablePriceTrendFilter_Daily, EnableMomentumTrendFilter_Daily);
            AddTimeframeData("Weekly", EnablePriceTrendFilter_Weekly, EnableMomentumTrendFilter_Weekly);

            // Add chart timeframe data if enabled
            if (EnableSMACycleFiltering || EnableMACDMoveFiltering)
            {
                try
                {
                    string chartInfo = "Chart: ";
                    bool hasData = false;

                    // Add chart SMA cycle data if enabled
                    if (EnableSMACycleFiltering)
                    {
                        var chartSMAData = GetDirectSMACycleData();
                        if (chartSMAData.IsValid)
                        {
                            chartInfo += $"Price {chartSMAData.Direction}#{chartSMAData.CycleCount}";
                            hasData = true;
                        }
                    }

                    // Add chart MACD data if enabled
                    if (EnableMACDMoveFiltering)
                    {
                        var chartMACDData = GetDirectChartMACDMoveData();
                        if (chartMACDData.IsValid)
                        {
                            string separator = hasData ? ", " : "";
                            chartInfo += $"{separator}MACD {chartMACDData.Direction}#{chartMACDData.MoveCount}";
                            hasData = true;
                        }
                    }

                    // Only add the line if we have data
                    if (hasData)
                    {
                        infoText.AppendLine(chartInfo);
                    }
                }
                catch (Exception ex)
                {
                    infoText.AppendLine($"Chart: Error - {ex.Message}");
                }
            }

            // Create unique box ID
            string boxId = "BounceData_" + Time[0].ToString("yyyyMMdd_HHmmss") + "_" + CurrentBar;

            // Position the box near the bounce bar
            double triggerLow = Low[0];
            double triggerHigh = High[0];
            double yOffset = isLongTrade ? triggerLow - (10 * TickSize) : triggerHigh + (10 * TickSize);

            // Draw info box with light background and black text for white chart backgrounds
            Draw.Text(this, boxId, false, infoText.ToString(), 0, yOffset, 0, Brushes.Black,
                     new Gui.Tools.SimpleFont("Arial", 10), TextAlignment.Left, Brushes.LightGray, Brushes.Black, 2);

            return boxId;
        }

        // Clear bounce validation data
        private void ClearBounceValidationData()
        {
            currentBounceM3Pass = false;
            currentBounceM9Pass = false;
            currentBounceSRPass = true;
            currentBounceBufferPass = true;
            currentBounceSMACyclePass = true;
            currentBounceBounceCountPass = true;
            currentBounceSMADirectionPass = true;
            currentBounceMACDDirectionPass = true;
            currentBounceMACDMovePass = true;
            currentBounceTrendFilterPass = true;
            currentBounceRejectionReason = "";
            currentBounceEntry = 0;
            currentBounceStopLoss = 0;
            currentBounceTarget = 0;
            currentBounceSize = 0;
            currentBounceDataReady = false;
            currentBounceIsLong = true;
        }
        
        // Create consolidated trade details box using stored validation data
        private string CreateConsolidatedTradeDetailsBoxFromStoredData()
        {
            if (!ShowTradeDetails || !currentBounceDataReady)
                return "";
                
            bounceCounter++;
            string boxId = $"ConsolidatedTradeDetails_{CurrentBar}_{bounceCounter}";
            
            string direction = currentBounceIsLong ? "LONG" : "SHORT";
            string triggerText = currentBounceIsLong ? "Close > 5MA" : "Close < 5MA";
            
            StringBuilder infoText = new StringBuilder();
            
            // Determine final result
            bool momentumPass = currentBounceM3Pass && currentBounceM9Pass;
            bool allPass = momentumPass && currentBounceSRPass && currentBounceBufferPass && currentBounceSMACyclePass && currentBounceBounceCountPass && currentBounceSMADirectionPass && currentBounceMACDDirectionPass && currentBounceMACDMovePass && currentBounceTrendFilterPass;
            
            string result = allPass ? "ACCEPTED" : "REJECTED";
            string resultIcon = allPass ? "✅" : "🚫";
            
            // Header
            infoText.AppendLine($"{resultIcon} {direction} {result}");
            infoText.AppendLine($"Time: {Time[0]:HH:mm:ss}");
            infoText.AppendLine($"{triggerText}: ✓");
            
            // Show rejection reason if applicable
            if (!allPass)
            {
                if (!momentumPass)
                {
                    string momentumReason = BuildMomentumRejectionReason(currentBounceIsLong, currentBounceM3Pass, currentBounceM9Pass);
                    infoText.AppendLine($"❌ REASON: {momentumReason}");
                }
                else if (!currentBounceSRPass)
                {
                    infoText.AppendLine($"❌ REASON: EMA34SR insufficient profit potential");
                }
                else if (!currentBounceBufferPass)
                {
                    infoText.AppendLine($"❌ REASON: EMA Buffer - {currentBounceRejectionReason}");
                }
                else if (!currentBounceSMACyclePass)
                {
                    infoText.AppendLine($"❌ REASON: {currentBounceRejectionReason}");
                }
                else if (!currentBounceSMADirectionPass)
                {
                    infoText.AppendLine($"❌ REASON: {currentBounceRejectionReason}");
                }
                else if (!currentBounceMACDDirectionPass)
                {
                    infoText.AppendLine($"❌ REASON: {currentBounceRejectionReason}");
                }
                else if (!currentBounceMACDMovePass)
                {
                    infoText.AppendLine($"❌ REASON: {currentBounceRejectionReason}");
                }
                else if (!currentBounceTrendFilterPass)
                {
                    infoText.AppendLine($"❌ REASON: {currentBounceRejectionReason}");
                }
            }
            infoText.AppendLine("");
            
            // Trade details
            infoText.AppendLine($"📊 Entry: {Instrument.MasterInstrument.FormatPrice(currentBounceEntry)}");
            if (currentBounceStopLoss > 0)
            {
                double riskPoints = Math.Abs(currentBounceEntry - currentBounceStopLoss);
                infoText.AppendLine($"Stop: {Instrument.MasterInstrument.FormatPrice(currentBounceStopLoss)}");
                infoText.AppendLine($"Risk: {Instrument.MasterInstrument.FormatPrice(riskPoints)} pts");
            }
            if (currentBounceTarget > 0)
            {
                double rewardPoints = Math.Abs(currentBounceTarget - currentBounceEntry);
                double rrRatio = (currentBounceStopLoss > 0) ? rewardPoints / Math.Abs(currentBounceEntry - currentBounceStopLoss) : 0;
                infoText.AppendLine($"Target: {Instrument.MasterInstrument.FormatPrice(currentBounceTarget)}");
                infoText.AppendLine($"Reward: {Instrument.MasterInstrument.FormatPrice(rewardPoints)} pts");
                if (rrRatio > 0)
                    infoText.AppendLine($"R/R: {rrRatio:F2}");
            }
            if (currentBounceSize > 0)
            {
                string unit = GetPositionSizeUnit();
                infoText.AppendLine($"Size: {currentBounceSize:F0} {unit}");
            }
            infoText.AppendLine("");
            
            // Validation results
            string m3Status = currentBounceM3Pass ? "✓" : "❌";
            string m9Status = currentBounceM9Pass ? "✓" : "❌";
            infoText.AppendLine($"📈 M3: {m3Status}  M9: {m9Status}");
            
            if (IsEMA34SREnabled())
            {
                string srStatus = currentBounceSRPass ? "✓" : "❌";
                infoText.AppendLine($"EMA34SR: {srStatus}");
            }
            
            if (IsEMABuffersEnabled())
            {
                string bufferStatus = currentBounceBufferPass ? "✓" : "❌";
                infoText.AppendLine($"EMA Buffer: {bufferStatus}");
            }
            
            // Position using same positioning as regular info boxes
            double triggerLow = currentBounceIsLong ? Low[0] : Low[0];
            double triggerHigh = currentBounceIsLong ? High[0] : High[0];
            double yOffset = currentBounceIsLong ? triggerLow - (6 * TickSize) : triggerHigh + (6 * TickSize);
            
            // Purple text for trade details
            Brush textColor = Brushes.Purple;
            
            // Create the info box
            Draw.Text(this, boxId, false, infoText.ToString(), 0, yOffset, 0, textColor, 
                     new Gui.Tools.SimpleFont("Arial", 11), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
            
            return boxId;
        }
        
        // Create consolidated trade details box using exact same momentum values as info box
        private string CreateConsolidatedTradeDetailsBoxWithMomentumValues(bool isLongTrade, bool m3FiltersPass, bool m9FiltersPass)
        {
            if (ShowDebugInfo)
                Print($"🟣 DEBUG: CreateConsolidatedTradeDetailsBoxWithMomentumValues called - isLong:{isLongTrade}, M3:{m3FiltersPass}, M9:{m9FiltersPass}");
                
            if (!ShowTradeDetails)
            {
                if (ShowDebugInfo)
                    Print($"🟣 DEBUG: ShowTradeDetails is FALSE - returning empty");
                return "";
            }
                
            bounceCounter++;
            string boxId = $"ConsolidatedTradeDetails_{CurrentBar}_{bounceCounter}";
            
            if (ShowDebugInfo)
                Print($"🟣 DEBUG: Creating trade details box with ID: {boxId}");
            
            string direction = isLongTrade ? "LONG" : "SHORT";
            string triggerText = isLongTrade ? "Close > 5MA" : "Close < 5MA";
            
            StringBuilder infoText = new StringBuilder();
            
            // Use PASSED momentum values (same as info box), stored values for other checks
            bool momentumPass = m3FiltersPass && m9FiltersPass;
            
            if (ShowDebugInfo)
                Print($"🟣 DEBUG: Momentum pass: {momentumPass}, SR pass: {currentBounceSRPass}, Buffer pass: {currentBounceBufferPass}, SMA Cycle pass: {currentBounceSMACyclePass}, Bounce Count pass: {currentBounceBounceCountPass}, SMA Direction pass: {currentBounceSMADirectionPass}, MACD Direction pass: {currentBounceMACDDirectionPass}, MACD Move pass: {currentBounceMACDMovePass}, Trend Filter pass: {currentBounceTrendFilterPass}");

            bool allPass = momentumPass && currentBounceSRPass && currentBounceBufferPass && currentBounceSMACyclePass && currentBounceBounceCountPass && currentBounceSMADirectionPass && currentBounceMACDDirectionPass && currentBounceMACDMovePass && currentBounceTrendFilterPass;
            
            string result = allPass ? "ACCEPTED" : "REJECTED";
            string resultIcon = allPass ? "✅" : "🚫";
            
            if (ShowDebugInfo)
                Print($"🟣 DEBUG: Final result: {result}, All pass: {allPass}");
            
            // Header
            infoText.AppendLine($"{resultIcon} {direction} {result}");
            infoText.AppendLine($"Time: {Time[0]:HH:mm:ss}");
            infoText.AppendLine($"{triggerText}: ✓");
            
            // Show rejection reason if applicable
            if (!allPass)
            {
                if (!momentumPass)
                {
                    string momentumReason = BuildMomentumRejectionReason(isLongTrade, m3FiltersPass, m9FiltersPass);
                    infoText.AppendLine($"❌ REASON: {momentumReason}");
                }
                else if (!currentBounceSRPass)
                {
                    string srRejectionDetail = GetEMA34SRRejectionDetails(isLongTrade, currentBounceEntry, currentBounceStopLoss, currentBounceTarget);
                    infoText.AppendLine($"❌ REASON: {srRejectionDetail}");
                }
                else if (!currentBounceBufferPass)
                {
                    string bufferRejectionDetail = GetEMABufferRejectionDetails(isLongTrade, currentBounceEntry);
                    infoText.AppendLine($"❌ REASON: {bufferRejectionDetail}");
                }
                else if (!currentBounceSMACyclePass)
                {
                    infoText.AppendLine($"❌ REASON: {currentBounceRejectionReason}");
                }
                else if (!currentBounceSMADirectionPass)
                {
                    infoText.AppendLine($"❌ REASON: {currentBounceRejectionReason}");
                }
                else if (!currentBounceMACDDirectionPass)
                {
                    infoText.AppendLine($"❌ REASON: {currentBounceRejectionReason}");
                }
                else if (!currentBounceMACDMovePass)
                {
                    infoText.AppendLine($"❌ REASON: {currentBounceRejectionReason}");
                }
                else if (!currentBounceTrendFilterPass)
                {
                    infoText.AppendLine($"❌ REASON: {currentBounceRejectionReason}");
                }
            }
            infoText.AppendLine("");
            
            // Trade details using stored values - with EMA34SR effective target adjustment
            if (ShowDebugInfo)
                Print($"🟣 DEBUG: Trade levels - Entry: {currentBounceEntry}, SL: {currentBounceStopLoss}, Target: {currentBounceTarget}, Size: {currentBounceSize}");
            
            // Calculate effective target considering EMA34SR blocking
            double effectiveTarget = GetEffectiveTarget(isLongTrade, currentBounceEntry, currentBounceTarget);
            bool targetAdjustedByEMA = Math.Abs(effectiveTarget - currentBounceTarget) > 0.1; // More than 0.1 point difference
                
            infoText.AppendLine($"📊 Entry: {Instrument.MasterInstrument.FormatPrice(currentBounceEntry)}");
            if (currentBounceStopLoss > 0)
            {
                double riskPoints = Math.Abs(currentBounceEntry - currentBounceStopLoss);
                infoText.AppendLine($"Stop: {Instrument.MasterInstrument.FormatPrice(currentBounceStopLoss)}");
                infoText.AppendLine($"Risk: {Instrument.MasterInstrument.FormatPrice(riskPoints)} pts");
            }
            if (currentBounceTarget > 0)
            {
                double rewardPoints = Math.Abs(effectiveTarget - currentBounceEntry);
                double rrRatio = (currentBounceStopLoss > 0) ? rewardPoints / Math.Abs(currentBounceEntry - currentBounceStopLoss) : 0;
                
                if (targetAdjustedByEMA)
                {
                    infoText.AppendLine($"Target: {Instrument.MasterInstrument.FormatPrice(effectiveTarget)} (EMA blocked)");
                    infoText.AppendLine($"Original: {Instrument.MasterInstrument.FormatPrice(currentBounceTarget)}");
                }
                else
                {
                    infoText.AppendLine($"Target: {Instrument.MasterInstrument.FormatPrice(effectiveTarget)}");
                }
                
                infoText.AppendLine($"Reward: {Instrument.MasterInstrument.FormatPrice(rewardPoints)} pts");
                if (rrRatio > 0)
                    infoText.AppendLine($"R/R: {rrRatio:F2}");
            }
            if (currentBounceSize > 0)
            {
                string unit = GetPositionSizeUnit();
                infoText.AppendLine($"Size: {currentBounceSize:F0} {unit}");
            }
            infoText.AppendLine("");
            
            // Validation results using PASSED momentum values (same as info box)
            string m3Status = m3FiltersPass ? "✓" : "❌";
            string m9Status = m9FiltersPass ? "✓" : "❌";
            infoText.AppendLine($"📈 M3: {m3Status}  M9: {m9Status}");
            
            if (IsEMA34SREnabled())
            {
                string srStatus = currentBounceSRPass ? "✓" : "❌";
                string srDetails = GetEMA34SRStatusDetails(isLongTrade, currentBounceEntry, currentBounceStopLoss, currentBounceTarget, currentBounceSRPass);
                infoText.AppendLine($"EMA34SR: {srStatus} {srDetails}");
            }
            
            if (IsEMABuffersEnabled())
            {
                string bufferStatus = currentBounceBufferPass ? "✓" : "❌";
                infoText.AppendLine($"EMA Buffer: {bufferStatus}");
            }
            
            // Position using same positioning as regular info boxes
            double triggerLow = Low[0];
            double triggerHigh = High[0];
            double yOffset = isLongTrade ? triggerLow - (6 * TickSize) : triggerHigh + (6 * TickSize);
            
            if (ShowDebugInfo)
                Print($"🟣 DEBUG: Positioning - triggerLow: {triggerLow}, triggerHigh: {triggerHigh}, yOffset: {yOffset}");
            
            // Purple text for trade details
            Brush textColor = Brushes.Purple;
            
            // Create the info box
            if (ShowDebugInfo)
                Print($"🟣 DEBUG: About to create Draw.Text with boxId: {boxId}");
                
            try
            {
                Draw.Text(this, boxId, false, infoText.ToString(), 0, yOffset, 0, textColor, 
                         new Gui.Tools.SimpleFont("Arial", 11), TextAlignment.Left, Brushes.Transparent, Brushes.Transparent, 0);
                         
                if (ShowDebugInfo)
                    Print($"🟣 DEBUG: ✅ Trade details box created successfully: {boxId}");
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"🔴 ERROR: Failed to create trade details box: {ex.Message}");
            }
            
            return boxId;
        }
        
        private (string MacdStatus, string StochStatus) GetM3FilterDetails(bool isLongTrade)
        {
            // MACD analysis - using direct indicator access
            double currentMACD = phantomM3MacdLine;
            double previousMACD = GetPreviousM3MacdLine();
            double macdSlope = currentMACD - previousMACD;
            
            string direction = isLongTrade ? "UP" : "DOWN";
            bool macdOK = isLongTrade ? (macdSlope > 0) : (macdSlope < 0);
            string actualDirection = macdSlope > 0 ? "UP" : "DOWN";
            
            string macdStatus;
            if (macdOK)
            {
                macdStatus = $"✓ {actualDirection} {previousMACD:F1}→{currentMACD:F1}";
            }
            else
            {
                // Check if momentum is weakening (same wrong direction but less steep)
                double prevPrevMACD = GetPrevPreviousM3MacdLine();
                double previousSlope = previousMACD - prevPrevMACD;
                bool momentumWeakening = false;
                
                if (isLongTrade) // Want UP, but current is DOWN
                {
                    if (macdSlope < 0 && previousSlope < 0)
                        momentumWeakening = macdSlope > previousSlope; // Less negative = weakening
                }
                else // Want DOWN, but current is UP
                {
                    if (macdSlope > 0 && previousSlope > 0)
                        momentumWeakening = macdSlope < previousSlope; // Less positive = weakening
                }
                
                if (momentumWeakening)
                {
                    macdStatus = $"✓ REDUCING {prevPrevMACD:F1}→{previousMACD:F1}→{currentMACD:F1} (was {Math.Abs(previousSlope):F1}, now {Math.Abs(macdSlope):F1})";
                }
                else
                {
                    macdStatus = $"❌ {actualDirection} {previousMACD:F1}→{currentMACD:F1} (need {direction})";
                }
            }
                
            // Stochastic analysis - using direct indicator access
            double currentK = phantomM3StochK;
            double previousK = GetPreviousM3StochK();
            double kSlope = currentK - previousK;
            
            string stochStatus;
            
            // Check extreme zones first - waive requirement if extreme
            if (currentK > 80 || currentK < 20)
            {
                stochStatus = $"✓ Extreme {previousK:F0}→{currentK:F0} - Waived";
            }
            else
            {
                // Normal zone - check slope direction
                bool angleMatches = isLongTrade ? (kSlope > 0) : (kSlope < 0);
                string actualStochDirection = kSlope > 0 ? "UP" : "DOWN";
                
                if (angleMatches)
                {
                    stochStatus = $"✓ {actualStochDirection} {previousK:F0}→{currentK:F0}";
                }
                else
                {
                    // Check if momentum is weakening (same wrong direction but less steep)
                    double prevPrevK = GetPrevPreviousM3StochK();
                    double previousSlope = previousK - prevPrevK;
                    bool momentumWeakening = false;

                    if (isLongTrade) // Want UP, but current is DOWN
                    {
                        if (kSlope < 0 && previousSlope < 0)
                            momentumWeakening = kSlope > previousSlope; // Less negative = weakening
                    }
                    else // Want DOWN, but current is UP
                    {
                        if (kSlope > 0 && previousSlope > 0)
                            momentumWeakening = kSlope < previousSlope; // Less positive = weakening
                    }

                    if (momentumWeakening)
                    {
                        stochStatus = $"✓ REDUCING {prevPrevK:F0}→{previousK:F0}→{currentK:F0} (was {Math.Abs(previousSlope):F1}, now {Math.Abs(kSlope):F1})";
                    }
                    else
                    {
                        stochStatus = $"❌ {actualStochDirection} {previousK:F0}→{currentK:F0} (need {direction})";
                    }
                }
            }
            
            return (macdStatus, stochStatus);
        }
        
        private (string MacdStatus, string StochStatus) GetM9FilterDetails(bool isLongTrade)
        {
            // MACD analysis - using direct indicator access
            double currentMACD = phantomM9MacdLine;
            double previousMACD = GetPreviousM9MacdLine();
            double macdSlope = currentMACD - previousMACD;
            
            string direction = isLongTrade ? "UP" : "DOWN";
            bool macdOK = isLongTrade ? (macdSlope > 0) : (macdSlope < 0);
            string actualDirection = macdSlope > 0 ? "UP" : "DOWN";
            
            string macdStatus;
            if (macdOK)
            {
                macdStatus = $"✓ {actualDirection} {previousMACD:F1}→{currentMACD:F1}";
            }
            else
            {
                // Check if momentum is weakening (same wrong direction but less steep)
                double prevPrevMACD = GetPrevPreviousM9MacdLine();
                double previousSlope = previousMACD - prevPrevMACD;
                bool momentumWeakening = false;
                
                if (isLongTrade) // Want UP, but current is DOWN
                {
                    if (macdSlope < 0 && previousSlope < 0)
                        momentumWeakening = macdSlope > previousSlope; // Less negative = weakening
                }
                else // Want DOWN, but current is UP
                {
                    if (macdSlope > 0 && previousSlope > 0)
                        momentumWeakening = macdSlope < previousSlope; // Less positive = weakening
                }
                
                if (momentumWeakening)
                {
                    macdStatus = $"✓ REDUCING {prevPrevMACD:F1}→{previousMACD:F1}→{currentMACD:F1} (was {Math.Abs(previousSlope):F1}, now {Math.Abs(macdSlope):F1})";
                }
                else
                {
                    macdStatus = $"❌ {actualDirection} {previousMACD:F1}→{currentMACD:F1} (need {direction})";
                }
            }
                
            // Stochastic analysis - using direct indicator access
            double currentK = phantomM9StochK;
            double previousK = GetPreviousM9StochK();
            double kSlope = currentK - previousK;
            
            string stochStatus;
            
            // Check extreme zones first - waive requirement if extreme
            if (currentK > 80 || currentK < 20)
            {
                stochStatus = $"✓ Extreme {previousK:F0}→{currentK:F0} - Waived";
            }
            else
            {
                // Normal zone - check slope direction
                bool angleMatches = isLongTrade ? (kSlope > 0) : (kSlope < 0);
                string actualStochDirection = kSlope > 0 ? "UP" : "DOWN";
                
                if (angleMatches)
                {
                    stochStatus = $"✓ {actualStochDirection} {previousK:F0}→{currentK:F0}";
                }
                else
                {
                    // Check if momentum is weakening (same wrong direction but less steep)
                    double prevPrevK = GetPrevPreviousM9StochK();
                    double previousSlope = previousK - prevPrevK;
                    bool momentumWeakening = false;

                    if (isLongTrade) // Want UP, but current is DOWN
                    {
                        if (kSlope < 0 && previousSlope < 0)
                            momentumWeakening = kSlope > previousSlope; // Less negative = weakening
                    }
                    else // Want DOWN, but current is UP
                    {
                        if (kSlope > 0 && previousSlope > 0)
                            momentumWeakening = kSlope < previousSlope; // Less positive = weakening
                    }

                    if (momentumWeakening)
                    {
                        stochStatus = $"✓ REDUCING {prevPrevK:F0}→{previousK:F0}→{currentK:F0} (was {Math.Abs(previousSlope):F1}, now {Math.Abs(kSlope):F1})";
                    }
                    else
                    {
                        stochStatus = $"❌ {actualStochDirection} {previousK:F0}→{currentK:F0} (need {direction})";
                    }
                }
            }
            
            return (macdStatus, stochStatus);
        }
        
        private string GetTimeFrameString(NinjaTrader.Data.BarsPeriod barsPeriod)
        {
            // Match the exact format used by MomentumAgeIndicator
            // Format: BarsPeriodType.ToString() + Value.ToString()
            // This creates "Minute1", "Minute3", etc. instead of "1MIN", "3MIN"
            return barsPeriod.BarsPeriodType.ToString() + barsPeriod.Value.ToString();
        }
        
        // PHANTOM BAR METHODS - Copied from OHLCTest.cs
        
        private void UpdatePhantomM3Bars()
        {
            DateTime currentM1Time = Time[0];
            DateTime currentNT8M3Time = Times[1][0];
            bool m3JustClosed = (lastSeenM3Time != currentNT8M3Time);
            
            // At each M1 close, check if M3 bar just closed
            if (m3JustClosed)
            {
                // Move everything in array up 1 place: [1]→[2], [2]→[3], etc
                ShiftPhantomArraysUp();
                
                // Copy NT8 completed bar at [0] to phantom array at [1]
                CopyCompletedM3ToPhantom();
                
                // Location [0] remains empty for now (will be filled as M1s accumulate)
                ClearFormingBarData();
                
                // Update tracking
                lastSeenM3Time = currentNT8M3Time;
            }
            
            // Only update phantom [0] with current M1 data if M3 has NOT closed
            if (!m3JustClosed)
            {
                UpdateFormingBarAtZero();
            }
            
            // Count available phantom bars for stochastic calculation
            int availableBars = 0;
            int startCount = m3JustClosed ? 1 : 0;  // Skip phantom[0] if M3 just closed
            for (int i = startCount; i < 20; i++)
            {
                if (phantomM3Closes[i] != 0) availableBars++;
                else break;
            }
            fastKCountM3 = availableBars;
            
            // Calculate manual stochastic using phantom bars (exact NT8 formula)
            CalculateManualStochastic(m3JustClosed);
            
            // Calculate manual MACD using phantom bars (exact NT8 formula)
            CalculateManualMACD(m3JustClosed);
        }
        
        private void ShiftPhantomArraysUp()
        {
            // Move everything up 1 place: [19] gets deleted, [1]→[2], [2]→[3], etc
            // Start from the top to avoid overwriting data we still need
            for (int i = 18; i >= 1; i--)
            {
                phantomM3Opens[i + 1] = phantomM3Opens[i];
                phantomM3Highs[i + 1] = phantomM3Highs[i];
                phantomM3Lows[i + 1] = phantomM3Lows[i];
                phantomM3Closes[i + 1] = phantomM3Closes[i];
                phantomM3Times[i + 1] = phantomM3Times[i];
            }
        }
        
        private void CopyCompletedM3ToPhantom()
        {
            phantomM3Opens[1] = Opens[1][0];
            phantomM3Highs[1] = Highs[1][0];
            phantomM3Lows[1] = Lows[1][0];
            phantomM3Closes[1] = Closes[1][0];
            phantomM3Times[1] = Times[1][0];
        }
        
        private void ClearFormingBarData()
        {
            // Clear phantom [0] - will be filled by UpdateFormingBarAtZero()
            phantomM3Opens[0] = 0;
            phantomM3Highs[0] = 0;
            phantomM3Lows[0] = double.MaxValue;
            phantomM3Closes[0] = 0;
            phantomM3Times[0] = DateTime.MinValue;
        }
        
        private void UpdateFormingBarAtZero()
        {
            // Open: only write if [0] is empty (first M1 after M3 close)
            if (phantomM3Opens[0] == 0)
            {
                phantomM3Opens[0] = Open[0];
            }
            
            // High: only update if current M1 high > existing high at [0]
            if (High[0] > phantomM3Highs[0])
            {
                phantomM3Highs[0] = High[0];
            }
            
            // Low: only update if current M1 low < existing low at [0]
            if (Low[0] < phantomM3Lows[0])
            {
                phantomM3Lows[0] = Low[0];
            }
            
            // Close: always overwrite with current M1 close
            phantomM3Closes[0] = Close[0];
            
            // Time: set to expected M3 closing time
            if (phantomM3Times[0] == DateTime.MinValue)
            {
                phantomM3Times[0] = lastSeenM3Time.AddMinutes(3);
            }
        }
        
        private void CalculateManualStochastic(bool m3JustClosed)
        {
            // Need at least stochK bars to calculate
            if (fastKCountM3 < stochK)
                return;
                
            int startIndex = m3JustClosed ? 1 : 0;  // Start from [1] if M3 closed, [0] otherwise
            
            // Find highest high and lowest low over last stochK phantom bars (NT8 formula)
            double highestHigh = double.MinValue;
            double lowestLow = double.MaxValue;
            
            for (int i = startIndex; i < startIndex + stochK && i < 20; i++)
            {
                if (phantomM3Highs[i] > highestHigh)
                    highestHigh = phantomM3Highs[i];
                if (phantomM3Lows[i] < lowestLow)
                    lowestLow = phantomM3Lows[i];
            }
            
            // Calculate %K using exact NT8 formula
            double currentClose = phantomM3Closes[startIndex];  // Use close from correct bar
            double nom = currentClose - lowestLow;
            double den = highestHigh - lowestLow;
            
            double fastK;
            if (den.ApproxCompare(0) == 0)
            {
                fastK = fastKCountM3 == 0 ? 50.0 : fastKValuesM3[(fastKIndexM3 - 1 + 50) % 50];
            }
            else
            {
                fastK = Math.Min(100, Math.Max(0, 100 * nom / den));
            }
            
            // Only store %K in ring buffer when M3 closes (not on every M1)
            if (m3JustClosed)
            {
                fastKValuesM3[fastKIndexM3] = fastK;
                fastKIndexM3 = (fastKIndexM3 + 1) % 50;
                if (fastKCountM3 < 50) fastKCountM3++;
            }
            
            // Calculate smoothed K (what NT8 calls %K)
            double smoothedK = 0;
            if (fastKCountM3 >= stochSmooth)
            {
                for (int i = 0; i < stochSmooth; i++)
                {
                    int idx = (fastKIndexM3 - 1 - i + 50) % 50;
                    smoothedK += fastKValuesM3[idx];
                }
                smoothedK /= stochSmooth;
            }
            else
            {
                smoothedK = fastK;
            }
            
            // Only store smoothed K in ring buffer when M3 closes (not on every M1)
            if (m3JustClosed)
            {
                smoothedKValuesM3[smoothedKIndexM3] = smoothedK;
                smoothedKIndexM3 = (smoothedKIndexM3 + 1) % 50;
                if (smoothedKCountM3 < 50) smoothedKCountM3++;
            }
            
            // Calculate %D (SMA of smoothed K values over stochD periods)
            double stochDValue = 0;
            if (smoothedKCountM3 >= stochD)
            {
                for (int i = 0; i < stochD; i++)
                {
                    int idx = (smoothedKIndexM3 - 1 - i + 50) % 50;
                    stochDValue += smoothedKValuesM3[idx];
                }
                stochDValue /= stochD;
            }
            else
            {
                stochDValue = smoothedK;
            }
            
            // Update phantom stochastic values
            phantomM3StochK = smoothedK;
            phantomM3StochD = stochDValue;
        }
        
        private void CalculateManualMACD(bool m3JustClosed)
        {
            // Use phantom M3 close data
            int startIndex = m3JustClosed ? 1 : 0;
            double currentClose = phantomM3Closes[startIndex];
            
            if (currentClose == 0) return; // No data yet
            
            // Calculate EMAs using exact NT8 formula from @MACD.cs
            double fastEma, slowEma;
            
            if (macdCountM3 == 0)
            {
                // First calculation - use close as seed (same as NT8)
                fastEma = slowEma = currentClose;
            }
            else
            {
                // Get previous EMA values for calculation
                int prevIdx = (macdIndexM3 - 1 + 200) % 200;
                double prevFastEma = fastEmaValuesM3[prevIdx];
                double prevSlowEma = slowEmaValuesM3[prevIdx];
                
                // Calculate new EMAs using exact NT8 formula:
                // fastEma = constant1 * input + constant2 * prevFastEma
                // slowEma = constant3 * input + constant4 * prevSlowEma
                fastEma = constant1 * currentClose + constant2 * prevFastEma;
                slowEma = constant3 * currentClose + constant4 * prevSlowEma;
            }
            
            // Calculate MACD Line = FastEMA - SlowEMA
            double macdLine = fastEma - slowEma;
            
            // Only store values in ring buffer when M3 closes
            if (m3JustClosed)
            {
                fastEmaValuesM3[macdIndexM3] = fastEma;
                slowEmaValuesM3[macdIndexM3] = slowEma;
                macdLineValuesM3[macdIndexM3] = macdLine;
            }
            
            // Calculate Signal Line using exact NT8 formula from @MACD.cs
            double signalLine;
            if (macdCountM3 == 0)
            {
                // First signal line = 0 (same as NT8: Avg[0] = 0)
                signalLine = 0;
            }
            else
            {
                // Get previous signal line for EMA calculation
                int prevIdx = (macdIndexM3 - 1 + 200) % 200;
                double prevSignalLine = signalLineValuesM3[prevIdx];
                
                // Calculate using exact NT8 formula: 
                // macdAvg = constant5 * macd + constant6 * Avg[1]
                signalLine = constant5 * macdLine + constant6 * prevSignalLine;
            }
            
            // Only store signal line when M3 closes
            if (m3JustClosed)
            {
                signalLineValuesM3[macdIndexM3] = signalLine;
                macdIndexM3 = (macdIndexM3 + 1) % 200;
                if (macdCountM3 < 200) macdCountM3++;
            }
            
            // Update phantom MACD values
            phantomM3MacdLine = macdLine;
            phantomM3SignalLine = signalLine;
        }
        
        // M9 PHANTOM BAR METHODS
        private void UpdatePhantomM9Bars()
        {
            DateTime currentM1Time = Time[0];
            DateTime currentNT8M9Time = Times[2][0];
            bool m9JustClosed = (lastSeenM9Time != currentNT8M9Time);
            
            // At each M1 close, check if M9 bar just closed
            if (m9JustClosed)
            {
                // Move everything in array up 1 place: [1]→[2], [2]→[3], etc
                ShiftPhantomM9ArraysUp();
                
                // Copy NT8 completed bar at [0] to phantom array at [1]
                CopyCompletedM9ToPhantom();
                
                // Location [0] remains empty for now (will be filled as M1s accumulate)
                ClearFormingM9BarData();
                
                // Update tracking
                lastSeenM9Time = currentNT8M9Time;
            }
            
            // Only update phantom [0] with current M1 data if M9 has NOT closed
            if (!m9JustClosed)
            {
                UpdateFormingM9BarAtZero();
            }
            
            // Count available phantom bars for stochastic calculation
            int availableBars = 0;
            int startCount = m9JustClosed ? 1 : 0;  // Skip phantom[0] if M9 just closed
            for (int i = startCount; i < 20; i++)
            {
                if (phantomM9Closes[i] != 0) availableBars++;
                else break;
            }
            fastKCountM9 = availableBars;
            
            // Calculate manual stochastic using phantom bars (exact NT8 formula)
            CalculateManualM9Stochastic(m9JustClosed);
            
            // Calculate manual MACD using phantom bars (exact NT8 formula)
            CalculateManualM9MACD(m9JustClosed);
        }
        
        private void ShiftPhantomM9ArraysUp()
        {
            // Move everything up 1 place: [19] gets deleted, [1]→[2], [2]→[3], etc
            // Start from the top to avoid overwriting data we still need
            for (int i = 18; i >= 1; i--)
            {
                phantomM9Opens[i + 1] = phantomM9Opens[i];
                phantomM9Highs[i + 1] = phantomM9Highs[i];
                phantomM9Lows[i + 1] = phantomM9Lows[i];
                phantomM9Closes[i + 1] = phantomM9Closes[i];
                phantomM9Times[i + 1] = phantomM9Times[i];
            }
        }
        
        private void CopyCompletedM9ToPhantom()
        {
            phantomM9Opens[1] = Opens[2][0];
            phantomM9Highs[1] = Highs[2][0];
            phantomM9Lows[1] = Lows[2][0];
            phantomM9Closes[1] = Closes[2][0];
            phantomM9Times[1] = Times[2][0];
        }
        
        private void ClearFormingM9BarData()
        {
            // Clear phantom [0] - will be filled by UpdateFormingM9BarAtZero()
            phantomM9Opens[0] = 0;
            phantomM9Highs[0] = 0;
            phantomM9Lows[0] = double.MaxValue;
            phantomM9Closes[0] = 0;
            phantomM9Times[0] = DateTime.MinValue;
        }
        
        private void UpdateFormingM9BarAtZero()
        {
            // Open: only write if [0] is empty (first M1 after M9 close)
            if (phantomM9Opens[0] == 0)
            {
                phantomM9Opens[0] = Open[0];
            }
            
            // High: only update if current M1 high > existing high at [0]
            if (High[0] > phantomM9Highs[0])
            {
                phantomM9Highs[0] = High[0];
            }
            
            // Low: only update if current M1 low < existing low at [0]
            if (Low[0] < phantomM9Lows[0])
            {
                phantomM9Lows[0] = Low[0];
            }
            
            // Close: always overwrite with current M1 close
            phantomM9Closes[0] = Close[0];
            
            // Time: set to expected M9 closing time
            if (phantomM9Times[0] == DateTime.MinValue)
            {
                phantomM9Times[0] = lastSeenM9Time.AddMinutes(9);
            }
        }
        
        private void CalculateManualM9Stochastic(bool m9JustClosed)
        {
            // Need at least stochK bars to calculate
            if (fastKCountM9 < stochK)
                return;
                
            int startIndex = m9JustClosed ? 1 : 0;  // Start from [1] if M9 closed, [0] otherwise
            
            // Find highest high and lowest low over stochK periods starting from startIndex
            double highestHigh = double.MinValue;
            double lowestLow = double.MaxValue;
            
            for (int i = startIndex; i < startIndex + stochK && i < 20; i++)
            {
                if (phantomM9Highs[i] > highestHigh)
                    highestHigh = phantomM9Highs[i];
                if (phantomM9Lows[i] < lowestLow)
                    lowestLow = phantomM9Lows[i];
            }
            
            // Calculate raw %K using exact NT8 formula
            double currentClose = phantomM9Closes[startIndex]; // Use close from startIndex (phantom[0] or phantom[1])
            double nom = currentClose - lowestLow;
            double den = highestHigh - lowestLow;
            
            double fastK;
            if (den.ApproxCompare(0) == 0)
            {
                fastK = fastKCountM9 == 0 ? 50.0 : fastKValuesM9[(fastKIndexM9 - 1 + 50) % 50];
            }
            else
            {
                fastK = Math.Min(100, Math.Max(0, 100 * nom / den));
            }
            
            // Only store raw %K values in ring buffer when M9 closes (not on every M1)
            if (m9JustClosed)
            {
                fastKValuesM9[fastKIndexM9] = fastK;
                fastKIndexM9 = (fastKIndexM9 + 1) % 50;
                if (fastKCountM9 < 50) fastKCountM9++;
            }
            
            // Calculate smoothed %K (what NT8 calls %K) using ring buffer
            double smoothedK = 0;
            if (fastKCountM9 >= stochSmooth)
            {
                for (int i = 0; i < stochSmooth; i++)
                {
                    int idx = (fastKIndexM9 - 1 - i + 50) % 50;
                    smoothedK += fastKValuesM9[idx];
                }
                smoothedK /= stochSmooth;
            }
            else
            {
                smoothedK = fastK;
            }
            
            // Only store smoothed %K values in ring buffer when M9 closes
            if (m9JustClosed)
            {
                smoothedKValuesM9[smoothedKIndexM9] = smoothedK;
                smoothedKIndexM9 = (smoothedKIndexM9 + 1) % 50;
                if (smoothedKCountM9 < 50) smoothedKCountM9++;
            }
            
            // Calculate %D (SMA of smoothed %K values over stochD periods)
            double stochDValue = 0;
            if (smoothedKCountM9 >= stochD)
            {
                for (int i = 0; i < stochD; i++)
                {
                    int idx = (smoothedKIndexM9 - 1 - i + 50) % 50;
                    stochDValue += smoothedKValuesM9[idx];
                }
                stochDValue /= stochD;
            }
            else
            {
                stochDValue = smoothedK;
            }
            
            // Update phantom M9 stochastic values
            phantomM9StochK = smoothedK;
            phantomM9StochD = stochDValue;
        }
        
        private void CalculateManualM9MACD(bool m9JustClosed)
        {
            // Need at least slow period bars to calculate MACD
            if (fastKCountM9 < macdSlow)
                return;
                
            int startIndex = m9JustClosed ? 1 : 0;  // Start from [1] if M9 closed, [0] otherwise
            double currentClose = phantomM9Closes[startIndex];
            
            // Calculate EMAs using exact NT8 formula from @MACD.cs
            double fastEma, slowEma;
            
            if (macdCountM9 == 0)
            {
                // First calculation - use close as seed (same as NT8)
                fastEma = slowEma = currentClose;
            }
            else
            {
                // Get previous EMA values for calculation
                int prevIdx = (macdIndexM9 - 1 + 200) % 200;
                double prevFastEma = fastEmaValuesM9[prevIdx];
                double prevSlowEma = slowEmaValuesM9[prevIdx];
                
                // Calculate new EMAs using exact NT8 formula:
                // fastEma = constant1 * input + constant2 * prevFastEma
                // slowEma = constant3 * input + constant4 * prevSlowEma
                fastEma = constant1 * currentClose + constant2 * prevFastEma;
                slowEma = constant3 * currentClose + constant4 * prevSlowEma;
            }
            
            // Calculate MACD Line = FastEMA - SlowEMA
            double macdLine = fastEma - slowEma;
            
            // Only store values in ring buffer when M9 closes
            if (m9JustClosed)
            {
                fastEmaValuesM9[macdIndexM9] = fastEma;
                slowEmaValuesM9[macdIndexM9] = slowEma;
                macdLineValuesM9[macdIndexM9] = macdLine;
            }
            
            // Calculate Signal Line using exact NT8 formula from @MACD.cs
            double signalLine;
            if (macdCountM9 == 0)
            {
                // First signal line = 0 (same as NT8: Avg[0] = 0)
                signalLine = 0;
            }
            else
            {
                // Get previous signal line for EMA calculation
                int prevIdx = (macdIndexM9 - 1 + 200) % 200;
                double prevSignalLine = signalLineValuesM9[prevIdx];
                
                // Calculate using exact NT8 formula: 
                // macdAvg = constant5 * macd + constant6 * Avg[1]
                signalLine = constant5 * macdLine + constant6 * prevSignalLine;
            }
            
            // Only store signal line when M9 closes
            if (m9JustClosed)
            {
                signalLineValuesM9[macdIndexM9] = signalLine;
                macdIndexM9 = (macdIndexM9 + 1) % 200;
                if (macdCountM9 < 200) macdCountM9++;
            }
            
            // Update phantom M9 MACD values
            phantomM9MacdLine = macdLine;
            phantomM9SignalLine = signalLine;
        }

        // Helper methods for historical phantom values
        private double GetPreviousM3MacdLine()
        {
            if (macdCountM3 < 2) return phantomM3MacdLine;
            return macdLineValuesM3[(macdIndexM3 - 1 + 200) % 200];
        }

        private double GetPrevPreviousM3MacdLine()
        {
            if (macdCountM3 < 3) return phantomM3MacdLine;
            return macdLineValuesM3[(macdIndexM3 - 2 + 200) % 200];
        }

        private double GetPreviousM3StochK()
        {
            if (smoothedKCountM3 < 2) return phantomM3StochK;
            return smoothedKValuesM3[(smoothedKIndexM3 - 1 + 50) % 50];
        }

        private double GetPrevPreviousM3StochK()
        {
            if (smoothedKCountM3 < 3) return phantomM3StochK;
            return smoothedKValuesM3[(smoothedKIndexM3 - 2 + 50) % 50];
        }

        private double GetPreviousM9MacdLine()
        {
            if (macdCountM9 < 2) return phantomM9MacdLine;
            return macdLineValuesM9[(macdIndexM9 - 1 + 200) % 200];
        }

        private double GetPrevPreviousM9MacdLine()
        {
            if (macdCountM9 < 3) return phantomM9MacdLine;
            return macdLineValuesM9[(macdIndexM9 - 2 + 200) % 200];
        }

        private double GetPreviousM9StochK()
        {
            if (smoothedKCountM9 < 2) return phantomM9StochK;
            return smoothedKValuesM9[(smoothedKIndexM9 - 1 + 50) % 50];
        }

        private double GetPrevPreviousM9StochK()
        {
            if (smoothedKCountM9 < 3) return phantomM9StochK;
            return smoothedKValuesM9[(smoothedKIndexM9 - 2 + 50) % 50];
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Min Bars Above EMA", Description = "Minimum bars with lows above EMA34 to confirm uptrend", Order = 1, GroupName = "Entry Settings")]
        public int MinBarsAboveEMA { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable M3 Momentum Filter", Description = "Require M3 MACD and Stochastic alignment for signals", Order = 2, GroupName = "Entry Settings")]
        public bool EnableM3Filter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable M9 Momentum Filter", Description = "Require M9 MACD or Stochastic alignment for signals", Order = 3, GroupName = "Entry Settings")]
        public bool EnableM9Filter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Monday Trading", Description = "Allow trades on Mondays", Order = 72, GroupName = "Z - Trading Times")]
        public bool EnableMonday { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Tuesday Trading", Description = "Allow trades on Tuesdays", Order = 73, GroupName = "Z - Trading Times")]
        public bool EnableTuesday { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Wednesday Trading", Description = "Allow trades on Wednesdays", Order = 74, GroupName = "Z - Trading Times")]
        public bool EnableWednesday { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Thursday Trading", Description = "Allow trades on Thursdays", Order = 75, GroupName = "Z - Trading Times")]
        public bool EnableThursday { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Friday Trading", Description = "Allow trades on Fridays", Order = 76, GroupName = "Z - Trading Times")]
        public bool EnableFriday { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Saturday Trading", Description = "Allow trades on Saturdays", Order = 77, GroupName = "Z - Trading Times")]
        public bool EnableSaturday { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Sunday Trading", Description = "Allow trades on Sundays", Order = 78, GroupName = "Z - Trading Times")]
        public bool EnableSunday { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Start Trading Time (HHMM)", Description = "Only take trades after this time in HHMM format (0 = disabled, e.g., 730 = 7:30 AM, 1430 = 2:30 PM)", Order = 79, GroupName = "Z - Trading Times")]
        public int StartTradingTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "End Trading Time (HHMM)", Description = "Only take trades before this time in HHMM format (0 = disabled, e.g., 1330 = 1:30 PM, 2100 = 9:00 PM)", Order = 80, GroupName = "Z - Trading Times")]
        public int EndTradingTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Signals", Description = "Display entry signals on chart", Order = 6, GroupName = "Display")]
        public bool ShowSignals { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Debug Info", Description = "Print debug messages to output window", Order = 3, GroupName = "Display")]
        public bool ShowDebugInfo { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Stochastic Cycle Debug", Description = "Print detailed stochastic cycle timing debug info", Order = 4, GroupName = "Display")]
        public bool ShowStochasticCycleDebug { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Info Boxes", Description = "Display detailed info boxes for each bounce", Order = 5, GroupName = "Display")]
        public bool ShowInfoBoxes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show MACD Counts", Description = "Display MACD trend count changes as boxes on chart", Order = 5, GroupName = "Display")]
        public bool ShowMACDCounts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show M3 MACD Counts", Description = "Display M3 MACD trend count changes as boxes on chart", Order = 6, GroupName = "Display")]
        public bool ShowM3MACDCounts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show M3 Price Trend Counts", Description = "Display M3 price trend cycle count changes as boxes on chart", Order = 7, GroupName = "Display")]
        public bool ShowM3PriceTrendCounts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Trade Details", Description = "Display detailed info boxes with entry/SL/TP levels for all triggers (accepted and rejected)", Order = 8, GroupName = "Display")]
        public bool ShowTradeDetails { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Price+MACD Trend State", Description = "Display info box showing price trend direction/count and MACD move numbers for all enabled timeframes at each bounce", Order = 9, GroupName = "Display")]
        public bool ShowBounceCriteria { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max Closes Against Trend", Description = "Maximum closes against trend before invalidating setup", Order = 14, GroupName = "Entry Settings")]
        public int MaxClosesAgainstTrend { get; set; }
        
        [NinjaScriptProperty]
        [Range(0.1, 3000.0)]
        [Display(Name = "ATR Buffer Percent", Description = "ATR buffer percentage for trigger levels", Order = 11, GroupName = "Entry Settings")]
        public double ATRBufferPercent { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 400)]
        [Display(Name = "Min Bars Between High/Low and Trigger", Description = "Minimum bars required between extreme price and trigger bar", Order = 12, GroupName = "Entry Settings")]
        public int MinBarsToTrigger { get; set; }
        
        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "Retry Bars After Block", Description = "Number of bars to retry entry after momentum filter block (0 = no retry)", Order = 13, GroupName = "Entry Settings")]
        public int RetryBars { get; set; }
        
        [NinjaScriptProperty]
        [Range(10.0, 200.0)]
        [Display(Name = "Stop Loss Buffer %", Description = "ATR buffer percentage for swing-based stop loss", Order = 9, GroupName = "Risk Management")]
        public double StopLossBufferPercent { get; set; }
        
        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Account Risk %", Description = "Risk percentage of account per trade", Order = 10, GroupName = "Risk Management")]
        public double AccountRiskPercent { get; set; }

        [NinjaScriptProperty] 
        [Range(1.0, 10.0)]
        [Display(Name = "Risk/Reward Ratio", Description = "Target risk/reward ratio for trades", Order = 11, GroupName = "Risk Management")]
        public double RiskRewardRatio { get; set; }

        [NinjaScriptProperty]
        [Range(1000, 10000000)]
        [Display(Name = "Account Size ($)", Description = "Manual account size override", Order = 12, GroupName = "Risk Management")]
        public double AccountSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Real Account Size", Description = "Read account size from file instead of manual setting", Order = 13, GroupName = "Risk Management")]
        public bool UseRealAccountSize { get; set; }
        
        [NinjaScriptProperty]
        [Display(Name = "Show Position Sizing", Description = "Display position sizing calculations in debug output", Order = 14, GroupName = "Risk Management")]
        public bool ShowPositionSizing { get; set; }
        
        [NinjaScriptProperty]
        [Display(Name = "Enable EMA34SR M15", Description = "Enable EMA34SR filtering for M15 timeframe", Order = 15, GroupName = "EMA34SR Integration")]
        public bool EnableEMA34SR_M15 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable EMA Buffers M15", Description = "Enable EMA buffer filtering for M15 timeframe", Order = 16, GroupName = "EMA34SR Integration")]
        public bool EnableEMABuffers_M15 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable EMA34SR M30", Description = "Enable EMA34SR filtering for M30 timeframe", Order = 17, GroupName = "EMA34SR Integration")]
        public bool EnableEMA34SR_M30 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable EMA Buffers M30", Description = "Enable EMA buffer filtering for M30 timeframe", Order = 18, GroupName = "EMA34SR Integration")]
        public bool EnableEMABuffers_M30 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable EMA34SR M60", Description = "Enable EMA34SR filtering for M60 timeframe", Order = 19, GroupName = "EMA34SR Integration")]
        public bool EnableEMA34SR_M60 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable EMA Buffers M60", Description = "Enable EMA buffer filtering for M60 timeframe", Order = 20, GroupName = "EMA34SR Integration")]
        public bool EnableEMABuffers_M60 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable EMA34SR M240", Description = "Enable EMA34SR filtering for M240 timeframe", Order = 21, GroupName = "EMA34SR Integration")]
        public bool EnableEMA34SR_M240 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable EMA Buffers M240", Description = "Enable EMA buffer filtering for M240 timeframe", Order = 22, GroupName = "EMA34SR Integration")]
        public bool EnableEMABuffers_M240 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable EMA34SR Daily", Description = "Enable EMA34SR filtering for Daily timeframe", Order = 23, GroupName = "EMA34SR Integration")]
        public bool EnableEMA34SR_Daily { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable EMA Buffers Daily", Description = "Enable EMA buffer filtering for Daily timeframe", Order = 24, GroupName = "EMA34SR Integration")]
        public bool EnableEMABuffers_Daily { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable EMA34SR Weekly", Description = "Enable EMA34SR filtering for Weekly timeframe", Order = 25, GroupName = "EMA34SR Integration")]
        public bool EnableEMA34SR_Weekly { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable EMA Buffers Weekly", Description = "Enable EMA buffer filtering for Weekly timeframe", Order = 26, GroupName = "EMA34SR Integration")]
        public bool EnableEMABuffers_Weekly { get; set; }
        
        [NinjaScriptProperty]
        [Range(5.0, 100.0)]
        [Display(Name = "EMA Buffer Percentage", Description = "Buffer percentage of distance between EMA High/Low for restriction zones", Order = 27, GroupName = "EMA34SR Integration")]
        public double EMABufferPercent { get; set; }
        
        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "Minimum Risk/Reward", Description = "Minimum risk/reward ratio required for trade approval", Order = 28, GroupName = "EMA34SR Integration")]
        public double MinimumRiskReward { get; set; }
        
        [NinjaScriptProperty]
        [Range(5.0, 200.0)]
        [Display(Name = "Distance from S/R (% of ATR)", Description = "Required distance from S/R levels as percentage of ATR", Order = 29, GroupName = "EMA34SR Integration")]
        public double DistanceFromSRAsATRPercent { get; set; }
        
        [NinjaScriptProperty]
        [Range(1.0, 500.0)]
        [Display(Name = "Max S/R Distance (Points)", Description = "Only consider S/R levels within X points of current price", Order = 30, GroupName = "EMA34SR Integration")]
        public double MaxSRDistancePoints { get; set; }
        
        [NinjaScriptProperty]
        [Display(Name = "Enable SMA Cycle Filtering", Description = "Filter trades based on SMA cycle count from MomentumAgeIndicator", Order = 30, GroupName = "SMA Cycle Filtering")]
        public bool EnableSMACycleFiltering { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max SMA Cycles", Description = "Maximum stochastic cycles since SMA direction change before blocking trades", Order = 31, GroupName = "SMA Cycle Filtering")]
        public int MaxSMACycles { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "SMA Period", Description = "Period for SMA direction tracking (should match MomentumAgeIndicator)", Order = 32, GroupName = "SMA Cycle Filtering")]
        public int SMACyclePeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Bounce Count Filter", Description = "Filter trades based on number of bounces since SMA direction change", Order = 33, GroupName = "SMA Cycle Filtering")]
        public bool EnableBounceCountFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max Bounces In SMA Direction", Description = "Maximum bounces in current SMA direction before blocking trades (only counts bounces aligned with SMA direction)", Order = 34, GroupName = "SMA Cycle Filtering")]
        public int MaxBouncesInSMADirection { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable SMA Direction Filter", Description = "Simple filter: Longs only when SMA up, Shorts only when SMA down (uses sophisticated direction tracking)", Order = 35, GroupName = "SMA Cycle Filtering")]
        public bool EnableSMADirectionFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable MACD Direction Filter", Description = "Simple filter: Longs only when MACD trend up, Shorts only when MACD trend down (uses MomentumAgeIndicator-style trend tracking)", Order = 36, GroupName = "SMA Cycle Filtering")]
        public bool EnableMACDDirectionFilter { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "MACD Reversal Threshold", Description = "Minimum reversal percentage (0.0-1.0) to confirm MACD trend change. 0.0=immediate, 0.1=10% reversal, 0.3=30% reversal", Order = 37, GroupName = "SMA Cycle Filtering")]
        public double MACDReversalThreshold { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable MACD Move Filtering", Description = "Filter trades based on chart timeframe MACD move count", Order = 38, GroupName = "SMA Cycle Filtering")]
        public bool EnableMACDMoveFiltering { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max MACD Moves", Description = "Maximum MACD trend moves before blocking trades", Order = 39, GroupName = "SMA Cycle Filtering")]
        public int MaxMACDMoves { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Price Trend Filter M3", Description = "Filter trades based on M3 price trend (50SMA direction)", Order = 36, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnablePriceTrendFilter_M3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Momentum Trend Filter M3", Description = "Filter trades based on M3 momentum trend (MACD trend)", Order = 36, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnableMomentumTrendFilter_M3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Price Trend Filter M9", Description = "Filter trades based on M9 price trend (50SMA direction)", Order = 37, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnablePriceTrendFilter_M9 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Momentum Trend Filter M9", Description = "Filter trades based on M9 momentum trend (MACD trend)", Order = 38, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnableMomentumTrendFilter_M9 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Price Trend Filter M15", Description = "Filter trades based on M15 price trend (50SMA direction)", Order = 39, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnablePriceTrendFilter_M15 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Momentum Trend Filter M15", Description = "Filter trades based on M15 momentum trend (MACD trend)", Order = 40, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnableMomentumTrendFilter_M15 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Price Trend Filter M30", Description = "Filter trades based on M30 price trend (50SMA direction)", Order = 41, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnablePriceTrendFilter_M30 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Momentum Trend Filter M30", Description = "Filter trades based on M30 momentum trend (MACD trend)", Order = 42, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnableMomentumTrendFilter_M30 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Price Trend Filter M60", Description = "Filter trades based on M60 price trend (50SMA direction)", Order = 43, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnablePriceTrendFilter_M60 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Momentum Trend Filter M60", Description = "Filter trades based on M60 momentum trend (MACD trend)", Order = 44, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnableMomentumTrendFilter_M60 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Price Trend Filter M240", Description = "Filter trades based on M240 price trend (50SMA direction)", Order = 45, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnablePriceTrendFilter_M240 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Momentum Trend Filter M240", Description = "Filter trades based on M240 momentum trend (MACD trend)", Order = 46, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnableMomentumTrendFilter_M240 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Price Trend Filter Daily", Description = "Filter trades based on Daily price trend (50SMA direction)", Order = 47, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnablePriceTrendFilter_Daily { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Momentum Trend Filter Daily", Description = "Filter trades based on Daily momentum trend (MACD trend)", Order = 48, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnableMomentumTrendFilter_Daily { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Price Trend Filter Weekly", Description = "Filter trades based on Weekly price trend (50SMA direction)", Order = 49, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnablePriceTrendFilter_Weekly { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Momentum Trend Filter Weekly", Description = "Filter trades based on Weekly momentum trend (MACD trend)", Order = 50, GroupName = "Multi-Timeframe Trend Filtering")]
        public bool EnableMomentumTrendFilter_Weekly { get; set; }


        [NinjaScriptProperty]
        [Display(Name = "Show Bounce Arrows", Description = "Draw arrows at bars where bounce criteria are met (regardless of momentum filters)", Order = 51, GroupName = "Display")]
        public bool ShowBounceArrows { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Bounce Counter", Description = "Display bounce counter number below each bounce arrow", Order = 52, GroupName = "Display")]
        public bool ShowBounceCounter { get; set; }

        // Cycle Limit Properties (14 properties for trend filtering)
        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max Price Trend Cycles M3", Description = "Maximum stochastic cycles allowed in current M3 SMA direction", Order = 52, GroupName = "Trend Cycle Limits")]
        public int MaxPriceTrendCycles_M3 { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max MACD Trend Moves M3", Description = "Maximum MACD trend moves allowed in current M3 MACD trend", Order = 53, GroupName = "Trend Cycle Limits")]
        public int MaxMACDTrendMoves_M3 { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max Price Trend Cycles M9", Description = "Maximum stochastic cycles allowed in current M9 SMA direction", Order = 54, GroupName = "Trend Cycle Limits")]
        public int MaxPriceTrendCycles_M9 { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max MACD Trend Moves M9", Description = "Maximum MACD trend moves allowed in current M9 MACD trend", Order = 55, GroupName = "Trend Cycle Limits")]
        public int MaxMACDTrendMoves_M9 { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max Price Trend Cycles M15", Description = "Maximum stochastic cycles allowed in current M15 SMA direction", Order = 56, GroupName = "Trend Cycle Limits")]
        public int MaxPriceTrendCycles_M15 { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max MACD Trend Moves M15", Description = "Maximum MACD trend moves allowed in current M15 MACD trend", Order = 57, GroupName = "Trend Cycle Limits")]
        public int MaxMACDTrendMoves_M15 { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max Price Trend Cycles M30", Description = "Maximum stochastic cycles allowed in current M30 SMA direction", Order = 58, GroupName = "Trend Cycle Limits")]
        public int MaxPriceTrendCycles_M30 { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max MACD Trend Moves M30", Description = "Maximum MACD trend moves allowed in current M30 MACD trend", Order = 59, GroupName = "Trend Cycle Limits")]
        public int MaxMACDTrendMoves_M30 { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max Price Trend Cycles M60", Description = "Maximum stochastic cycles allowed in current M60 SMA direction", Order = 60, GroupName = "Trend Cycle Limits")]
        public int MaxPriceTrendCycles_M60 { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max MACD Trend Moves M60", Description = "Maximum MACD trend moves allowed in current M60 MACD trend", Order = 61, GroupName = "Trend Cycle Limits")]
        public int MaxMACDTrendMoves_M60 { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max Price Trend Cycles M240", Description = "Maximum stochastic cycles allowed in current M240 SMA direction", Order = 62, GroupName = "Trend Cycle Limits")]
        public int MaxPriceTrendCycles_M240 { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max MACD Trend Moves M240", Description = "Maximum MACD trend moves allowed in current M240 MACD trend", Order = 63, GroupName = "Trend Cycle Limits")]
        public int MaxMACDTrendMoves_M240 { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max Price Trend Cycles Daily", Description = "Maximum stochastic cycles allowed in current Daily SMA direction", Order = 64, GroupName = "Trend Cycle Limits")]
        public int MaxPriceTrendCycles_Daily { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max MACD Trend Moves Daily", Description = "Maximum MACD trend moves allowed in current Daily MACD trend", Order = 65, GroupName = "Trend Cycle Limits")]
        public int MaxMACDTrendMoves_Daily { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max Price Trend Cycles Weekly", Description = "Maximum stochastic cycles allowed in current Weekly SMA direction", Order = 66, GroupName = "Trend Cycle Limits")]
        public int MaxPriceTrendCycles_Weekly { get; set; } = 50;

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Max MACD Trend Moves Weekly", Description = "Maximum MACD trend moves allowed in current Weekly MACD trend", Order = 67, GroupName = "Trend Cycle Limits")]
        public int MaxMACDTrendMoves_Weekly { get; set; } = 50;

        // Additional Configuration Properties (4 properties)
        [NinjaScriptProperty]
        [Display(Name = "SMA Flat Threshold", Description = "Minimum slope to consider SMA directional (ignores flat periods)", Order = 68, GroupName = "Trend Cycle Limits")]
        public double SMAFlatThreshold { get; set; } = 0.01d;

        [NinjaScriptProperty]
        [Display(Name = "SMA Direction Consecutive Bars", Description = "Number of consecutive bars required to confirm SMA direction change", Order = 69, GroupName = "Trend Cycle Limits")]
        public int SMADirectionConsecutiveBars { get; set; } = 3;

        [NinjaScriptProperty]
        [Display(Name = "Cycle Progression Buffer %", Description = "ATR percentage buffer required for cycle progression validation", Order = 70, GroupName = "Trend Cycle Limits")]
        public double CycleProgressionBufferPercent { get; set; } = 50.0d;

        [NinjaScriptProperty]
        [Display(Name = "Enable Cycle Progression Validation", Description = "Enable price progression validation for cycle counting", Order = 71, GroupName = "Trend Cycle Limits")]
        public bool EnableCycleProgressionValidation { get; set; } = true;
        #endregion
        
        #region EMA34SR Helper Methods
        
        private bool IsEMA34SREnabled()
        {
            // Return true if ANY of the EMA34SR timeframe toggles are enabled
            // This allows reading from multiple timeframe files simultaneously
            bool isEnabled = EnableEMA34SR_M15 || EnableEMA34SR_M30 || EnableEMA34SR_M60 || 
                            EnableEMA34SR_M240 || EnableEMA34SR_Daily || EnableEMA34SR_Weekly;
            
            if (ShowDebugInfo && CurrentBar % 50 == 0) // Print occasionally to avoid spam
            {
                Print($"🔧 [DEBUG] EMA34SR Toggle Status: M15={EnableEMA34SR_M15}, M30={EnableEMA34SR_M30}, M60={EnableEMA34SR_M60}, M240={EnableEMA34SR_M240}, Daily={EnableEMA34SR_Daily}, Weekly={EnableEMA34SR_Weekly}");
                Print($"🔧 [DEBUG] IsEMA34SREnabled() = {isEnabled}");
            }
            
            return isEnabled;
        }

        private bool IsWithinTradingHours()
        {
            // If both times are 0, time filtering is disabled - trade all day
            if (StartTradingTime == 0 && EndTradingTime == 0)
            {
                if (ShowDebugInfo && CurrentBar % 100 == 0)
                    Print($"⏰ Time filter DISABLED (both times are 0)");
                return true;
            }

            // Convert HHMM integer to TimeSpan for comparison
            // Example: 730 = 7:30 AM, 1430 = 2:30 PM, 2100 = 9:00 PM
            TimeSpan startTime = TimeSpan.Zero;
            TimeSpan endTime = TimeSpan.Zero;

            if (StartTradingTime > 0)
            {
                int startHour = StartTradingTime / 100;
                int startMin = StartTradingTime % 100;
                startTime = new TimeSpan(startHour, startMin, 0);
            }

            if (EndTradingTime > 0)
            {
                int endHour = EndTradingTime / 100;
                int endMin = EndTradingTime % 100;
                endTime = new TimeSpan(endHour, endMin, 0);
            }

            // Get current bar time (this is the historical bar's timestamp, not system time)
            TimeSpan currentTime = Time[0].TimeOfDay;

            if (ShowDebugInfo && CurrentBar % 100 == 0)
            {
                Print($"⏰ Time filter check:");
                Print($"   Bar Time: {Time[0]:HH:mm:ss}");
                Print($"   Start Time: {StartTradingTime} ({startTime})");
                Print($"   End Time: {EndTradingTime} ({endTime})");
                Print($"   Current Time: {currentTime}");
            }

            // If only start time is set (end time is 0)
            if (EndTradingTime == 0)
                return currentTime >= startTime;

            // If only end time is set (start time is 0)
            if (StartTradingTime == 0)
                return currentTime <= endTime;

            // Both times are set - check if current time is within the window
            // Handle normal case (e.g., 0730 to 1330 = 7:30 AM to 1:30 PM)
            if (startTime < endTime)
            {
                return currentTime >= startTime && currentTime <= endTime;
            }
            // Handle overnight case (e.g., 2200 to 0200 = 10:00 PM to 2:00 AM crosses midnight)
            else
            {
                return currentTime >= startTime || currentTime <= endTime;
            }
        }

        private bool IsValidTradingDay()
        {
            // Get the day of week for the current bar (uses bar time, not system time)
            DayOfWeek dayOfWeek = Time[0].DayOfWeek;

            bool isValid = false;
            string dayName = "";

            switch (dayOfWeek)
            {
                case DayOfWeek.Monday:
                    isValid = EnableMonday;
                    dayName = "Monday";
                    break;
                case DayOfWeek.Tuesday:
                    isValid = EnableTuesday;
                    dayName = "Tuesday";
                    break;
                case DayOfWeek.Wednesday:
                    isValid = EnableWednesday;
                    dayName = "Wednesday";
                    break;
                case DayOfWeek.Thursday:
                    isValid = EnableThursday;
                    dayName = "Thursday";
                    break;
                case DayOfWeek.Friday:
                    isValid = EnableFriday;
                    dayName = "Friday";
                    break;
                case DayOfWeek.Saturday:
                    isValid = EnableSaturday;
                    dayName = "Saturday";
                    break;
                case DayOfWeek.Sunday:
                    isValid = EnableSunday;
                    dayName = "Sunday";
                    break;
            }

            if (ShowDebugInfo && CurrentBar % 100 == 0)
            {
                Print($"📅 Day filter check:");
                Print($"   Bar Day: {Time[0]:ddd yyyy-MM-dd} ({dayName})");
                Print($"   Day Filter: {(isValid ? "PASS" : "BLOCKED")}");
            }

            return isValid;
        }

        private bool IsEMABuffersEnabled()
        {
            // Return true if ANY of the EMA buffer timeframe toggles are enabled
            return EnableEMABuffers_M15 || EnableEMABuffers_M30 || EnableEMABuffers_M60 ||
                   EnableEMABuffers_M240 || EnableEMABuffers_Daily || EnableEMABuffers_Weekly;
        }
        
        private bool IsTrendFilteringEnabled()
        {
            // Return true if ANY of the trend filter toggles are enabled
            return EnablePriceTrendFilter_M3 || EnableMomentumTrendFilter_M3 ||
                   EnablePriceTrendFilter_M9 || EnableMomentumTrendFilter_M9 ||
                   EnablePriceTrendFilter_M15 || EnableMomentumTrendFilter_M15 ||
                   EnablePriceTrendFilter_M30 || EnableMomentumTrendFilter_M30 ||
                   EnablePriceTrendFilter_M60 || EnableMomentumTrendFilter_M60 ||
                   EnablePriceTrendFilter_M240 || EnableMomentumTrendFilter_M240 ||
                   EnablePriceTrendFilter_Daily || EnableMomentumTrendFilter_Daily ||
                   EnablePriceTrendFilter_Weekly || EnableMomentumTrendFilter_Weekly;
        }
        
        private (bool IsAllowed, string RejectionReason) ValidateTrendFiltering(bool isLongTrade)
        {
            if (!IsTrendFilteringEnabled())
                return (true, "");

            var rejectionReasons = new List<string>();

            // Check each enabled timeframe with sophisticated cycle limits
            var timeframesToCheck = new[]
            {
                ("Minute3", "M3", EnablePriceTrendFilter_M3, EnableMomentumTrendFilter_M3, MaxPriceTrendCycles_M3, MaxMACDTrendMoves_M3),
                ("Minute9", "M9", EnablePriceTrendFilter_M9, EnableMomentumTrendFilter_M9, MaxPriceTrendCycles_M9, MaxMACDTrendMoves_M9),
                ("Minute15", "M15", EnablePriceTrendFilter_M15, EnableMomentumTrendFilter_M15, MaxPriceTrendCycles_M15, MaxMACDTrendMoves_M15),
                ("Minute30", "M30", EnablePriceTrendFilter_M30, EnableMomentumTrendFilter_M30, MaxPriceTrendCycles_M30, MaxMACDTrendMoves_M30),
                ("Minute60", "M60", EnablePriceTrendFilter_M60, EnableMomentumTrendFilter_M60, MaxPriceTrendCycles_M60, MaxMACDTrendMoves_M60),
                ("Minute240", "M240", EnablePriceTrendFilter_M240, EnableMomentumTrendFilter_M240, MaxPriceTrendCycles_M240, MaxMACDTrendMoves_M240),
                ("Day1", "Daily", EnablePriceTrendFilter_Daily, EnableMomentumTrendFilter_Daily, MaxPriceTrendCycles_Daily, MaxMACDTrendMoves_Daily),
                ("Week1", "Weekly", EnablePriceTrendFilter_Weekly, EnableMomentumTrendFilter_Weekly, MaxPriceTrendCycles_Weekly, MaxMACDTrendMoves_Weekly)
            };

            foreach (var timeframeData in timeframesToCheck)
            {
                string timeframe = timeframeData.Item1;
                string timeframeKey = timeframeData.Item2;
                bool priceTrendEnabled = timeframeData.Item3;
                bool momentumTrendEnabled = timeframeData.Item4;
                int maxPriceCycles = timeframeData.Item5;
                int maxMACDMoves = timeframeData.Item6;

                if (priceTrendEnabled)
                {
                    // Use sophisticated SMA direction and cycle counting
                    var smaState = GetSMADirectionState(timeframeKey);
                    var priceTrendResult = GetDirectPriceTrendData(timeframe);
                    string priceDirection = priceTrendResult.Item1;
                    bool priceValid = priceTrendResult.Item3;

                    if (priceValid)
                    {
                        // Check direction alignment
                        bool priceAligned = (isLongTrade && priceDirection == "Up") || (!isLongTrade && priceDirection == "Down");
                        if (!priceAligned)
                        {
                            rejectionReasons.Add($"{timeframe} Price Trend: {priceDirection} (need {(isLongTrade ? "Up" : "Down")})");
                        }

                        // Check sophisticated cycle limit
                        if (priceAligned && smaState.cyclesSinceDirectionChange > maxPriceCycles)
                        {
                            rejectionReasons.Add($"{timeframe} Price Cycles: {smaState.cyclesSinceDirectionChange} > {maxPriceCycles} (trend too old)");
                        }

                        if (ShowDebugInfo && timeframe == "Minute15") // Debug only M15 to avoid spam
                            Print($"[{timeframe}] SMA Filter: Direction={priceDirection}, Cycles={smaState.cyclesSinceDirectionChange}/{maxPriceCycles}, Aligned={priceAligned}");
                    }
                }

                if (momentumTrendEnabled)
                {
                    // Use sophisticated MACD trend move counting
                    var macdState = GetMACDTrendState(timeframeKey);
                    var macdTrendResult = GetDirectMACDTrendData(timeframe);
                    string macdDirection = macdTrendResult.Item1;
                    bool macdValid = macdTrendResult.Item3;

                    if (macdValid)
                    {
                        // Check direction alignment
                        bool momentumAligned = (isLongTrade && macdDirection == "Up") || (!isLongTrade && macdDirection == "Down");
                        if (!momentumAligned)
                        {
                            rejectionReasons.Add($"{timeframe} MACD Trend: {macdDirection} (need {(isLongTrade ? "Up" : "Down")})");
                        }

                        // Check sophisticated trend move limit
                        if (momentumAligned && macdState.trendMovesSinceDirectionChange > maxMACDMoves)
                        {
                            rejectionReasons.Add($"{timeframe} MACD Moves: {macdState.trendMovesSinceDirectionChange} > {maxMACDMoves} (momentum too old)");
                        }

                        if (ShowDebugInfo && timeframe == "Minute15") // Debug only M15 to avoid spam
                            Print($"[{timeframe}] MACD Filter: Direction={macdDirection}, Moves={macdState.trendMovesSinceDirectionChange}/{maxMACDMoves}, Aligned={momentumAligned}");
                    }
                }
            }

            if (rejectionReasons.Count > 0)
            {
                string combinedReason = $"Advanced trend filter rejection: {string.Join(", ", rejectionReasons)}";
                return (false, combinedReason);
            }

            return (true, "");
        }
        
        #endregion

        #region Multi-Timeframe Trend Calculation Methods

        private void CalculateHigherTimeframeTrends()
        {
            try
            {
                // ✅ FIXED: Add chart timeframe to advanced cycle tracking (like other timeframes)
                if (EnableSMACycleFiltering || EnableMACDDirectionFilter || EnableMACDMoveFiltering)
                    CalculateTrendsForTimeframe("Chart", smaCycle, chartMacd, ref currentSMADirection, ref chartMACDTrend, ref smaCycleCount, ref chartMACDMoveCount);

                // Calculate trends for each enabled timeframe
                if (EnablePriceTrendFilter_M3 || EnableMomentumTrendFilter_M3)
                    CalculateTrendsForTimeframe("M3", sma_M3, macd_M3, ref priceTrend_M3, ref macdTrend_M3, ref priceTrendNumber_M3, ref macdTrendNumber_M3);

                if (EnablePriceTrendFilter_M9 || EnableMomentumTrendFilter_M9)
                    CalculateTrendsForTimeframe("M9", sma_M9, macd_M9, ref priceTrend_M9, ref macdTrend_M9, ref priceTrendNumber_M9, ref macdTrendNumber_M9);

                if (EnablePriceTrendFilter_M15 || EnableMomentumTrendFilter_M15)
                    CalculateTrendsForTimeframe("M15", sma_M15, macd_M15, ref priceTrend_M15, ref macdTrend_M15, ref priceTrendNumber_M15, ref macdTrendNumber_M15);

                if (EnablePriceTrendFilter_M30 || EnableMomentumTrendFilter_M30)
                    CalculateTrendsForTimeframe("M30", sma_M30, macd_M30, ref priceTrend_M30, ref macdTrend_M30, ref priceTrendNumber_M30, ref macdTrendNumber_M30);

                if (EnablePriceTrendFilter_M60 || EnableMomentumTrendFilter_M60)
                    CalculateTrendsForTimeframe("M60", sma_M60, macd_M60, ref priceTrend_M60, ref macdTrend_M60, ref priceTrendNumber_M60, ref macdTrendNumber_M60);

                if (EnablePriceTrendFilter_M240 || EnableMomentumTrendFilter_M240)
                    CalculateTrendsForTimeframe("M240", sma_M240, macd_M240, ref priceTrend_M240, ref macdTrend_M240, ref priceTrendNumber_M240, ref macdTrendNumber_M240);

                if (EnablePriceTrendFilter_Daily || EnableMomentumTrendFilter_Daily)
                    CalculateTrendsForTimeframe("Daily", sma_Daily, macd_Daily, ref priceTrend_Daily, ref macdTrend_Daily, ref priceTrendNumber_Daily, ref macdTrendNumber_Daily);

                if (EnablePriceTrendFilter_Weekly || EnableMomentumTrendFilter_Weekly)
                    CalculateTrendsForTimeframe("Weekly", sma_Weekly, macd_Weekly, ref priceTrend_Weekly, ref macdTrend_Weekly, ref priceTrendNumber_Weekly, ref macdTrendNumber_Weekly);
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error calculating higher timeframe trends: {ex.Message}");
            }
        }

        private void CalculateTrendsForTimeframe(string timeframeName, SMA smaIndicator, MACD macdIndicator,
            ref string priceTrend, ref string macdTrend, ref int priceTrendNumber, ref int macdTrendNumber)
        {
            try
            {
                ATR atrIndicator = GetATRIndicator(timeframeName);
                Stochastics stochIndicator = GetStochasticIndicator(timeframeName);

                // Update sophisticated SMA direction tracking
                if (smaIndicator != null && CurrentBars[GetBarsArrayIndex(timeframeName)] >= SMACyclePeriod + 1)
                {
                    UpdateSMADirectionTracking(timeframeName, smaIndicator, atrIndicator);

                    // Get sophisticated SMA state
                    var smaState = GetSMADirectionState(timeframeName);

                    // Convert sophisticated direction to legacy string format for compatibility
                    if (smaState.lastNonFlatDirection == SMADirection.Up)
                        priceTrend = "Up";
                    else if (smaState.lastNonFlatDirection == SMADirection.Down)
                        priceTrend = "Down";
                    else
                        priceTrend = "Unknown";

                    // Use sophisticated cycle count instead of simple increment
                    priceTrendNumber = smaState.cyclesSinceDirectionChange;
                }

                // ✅ FIXED: MACD trend tracking - breakout detection runs every bar
                if (macdIndicator != null && CurrentBars[GetBarsArrayIndex(timeframeName)] >= 20 + 30)
                {
                    // Get MACD state (extremes updated by stochastic cycle detection)
                    var macdState = GetMACDTrendState(timeframeName);
                    double currentMacd = macdIndicator[0];

                    // Check for trend breakouts every bar based on cycle extremes
                    bool trendChanged = false;

                    if (macdState.currentTrend == MomentumTrend.Up)
                    {
                        // In uptrend: Check if MACD breaks below the last MACD low (from stochastic cycle)
                        if (macdState.lastMACDLow != 0)
                        {
                            double breakThreshold = macdState.lastMACDLow * (1.0 - MACDReversalThreshold);
                            if (currentMacd < breakThreshold)
                            {
                                macdState.currentTrend = MomentumTrend.Down;
                                trendChanged = true;
                                Print($"🔴 [{Time[0]:yyyy-MM-dd HH:mm}] [{timeframeName}] MACD Trend: UP → DOWN (broke below cycle low={macdState.lastMACDLow:F4}, threshold={MACDReversalThreshold:F2}), MACD={currentMacd:F4}");
                            }
                        }
                    }
                    else if (macdState.currentTrend == MomentumTrend.Down)
                    {
                        // In downtrend: Check if MACD breaks above the last MACD high (from stochastic cycle)
                        if (macdState.lastMACDHigh != 0)
                        {
                            double breakThreshold = macdState.lastMACDHigh * (1.0 + MACDReversalThreshold);
                            if (currentMacd > breakThreshold)
                            {
                                macdState.currentTrend = MomentumTrend.Up;
                                trendChanged = true;
                                Print($"🟢 [{Time[0]:yyyy-MM-dd HH:mm}] [{timeframeName}] MACD Trend: DOWN → UP (broke above cycle high={macdState.lastMACDHigh:F4}, threshold={MACDReversalThreshold:F2}), MACD={currentMacd:F4}");
                            }
                        }
                    }

                    // Convert sophisticated trend to legacy string format for compatibility
                    if (macdState.currentTrend == MomentumTrend.Up)
                        macdTrend = "Up";
                    else if (macdState.currentTrend == MomentumTrend.Down)
                        macdTrend = "Down";
                    else
                        macdTrend = "Unknown";

                    // Use sophisticated trend move count (updated only by stochastic cycles)
                    macdTrendNumber = macdState.trendMovesSinceDirectionChange;
                }

                // Update stochastic cycle detection for this timeframe
                if (stochIndicator != null && CurrentBars[GetBarsArrayIndex(timeframeName)] >= 10)
                {
                    UpdateStochasticCycleDetection(timeframeName, stochIndicator, atrIndicator);
                }

                if (ShowDebugInfo && timeframeName == "M15") // Only debug M15 to avoid spam
                {
                    var smaState = GetSMADirectionState(timeframeName);
                    var macdState = GetMACDTrendState(timeframeName);
                    Print($"🔍 [{timeframeName}] SMA: {priceTrend} (Cycles: {smaState.cyclesSinceDirectionChange}), MACD: {macdTrend} (Moves: {macdState.trendMovesSinceDirectionChange})");
                }
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error calculating trends for {timeframeName}: {ex.Message}");
            }
        }

        // Helper method to get ATR indicator for a timeframe
        private ATR GetATRIndicator(string timeframeName)
        {
            switch (timeframeName)
            {
                case "Chart": return atr; // Chart timeframe uses M1 ATR
                case "M3": return atr14_M3;
                case "M9": return atr14_M9;
                case "M15": return atr14_M15;
                case "M30": return atr14_M30;
                case "M60": return atr14_M60;
                case "M240": return atr14_M240;
                case "Daily": return atr14_Daily;
                case "Weekly": return atr14_Weekly;
                default: return atr; // Fallback to M1 ATR
            }
        }

        // Helper method to get Stochastic indicator for a timeframe
        private Stochastics GetStochasticIndicator(string timeframeName)
        {
            switch (timeframeName)
            {
                case "Chart": return stochM1; // Chart timeframe uses M1 stochastic
                case "M3": return stochM3; // Use existing M3 stochastic
                case "M9": return stochM9; // Use existing M9 stochastic
                case "M15": return stoch_M15;
                case "M30": return stoch_M30;
                case "M60": return stoch_M60;
                case "M240": return stoch_M240;
                case "Daily": return stoch_Daily;
                case "Weekly": return stoch_Weekly;
                default: return stochM1; // Fallback to M1 stochastic
            }
        }

        private int GetBarsArrayIndex(string timeframeName)
        {
            // Return the BarsArray index for the given timeframe
            // This matches the order from State.Configure data series additions
            if (timeframeName == "Chart") return 0; // Chart timeframe uses primary bars (index 0)

            // ✅ FIX: Add missing M3 and M9 cases
            if (timeframeName == "M3") return 1; // M3 is BarsArray[1]
            if (timeframeName == "M9") return 2; // M9 is BarsArray[2]

            int baseIndex = 3; // Start after M1(0), M3(1), M9(2)

            // Count enabled timeframes before this one to get correct index
            switch (timeframeName)
            {
                case "M15":
                    return baseIndex; // First enabled timeframe
                case "M30":
                    if (EnableEMA34SR_M15 || EnableEMABuffers_M15 || EnablePriceTrendFilter_M15 || EnableMomentumTrendFilter_M15)
                        baseIndex++;
                    return baseIndex;
                case "M60":
                    if (EnableEMA34SR_M15 || EnableEMABuffers_M15 || EnablePriceTrendFilter_M15 || EnableMomentumTrendFilter_M15)
                        baseIndex++;
                    if (EnableEMA34SR_M30 || EnableEMABuffers_M30 || EnablePriceTrendFilter_M30 || EnableMomentumTrendFilter_M30)
                        baseIndex++;
                    return baseIndex;
                case "M240":
                    if (EnableEMA34SR_M15 || EnableEMABuffers_M15 || EnablePriceTrendFilter_M15 || EnableMomentumTrendFilter_M15)
                        baseIndex++;
                    if (EnableEMA34SR_M30 || EnableEMABuffers_M30 || EnablePriceTrendFilter_M30 || EnableMomentumTrendFilter_M30)
                        baseIndex++;
                    if (EnableEMA34SR_M60 || EnableEMABuffers_M60 || EnablePriceTrendFilter_M60 || EnableMomentumTrendFilter_M60)
                        baseIndex++;
                    return baseIndex;
                case "Daily":
                    if (EnableEMA34SR_M15 || EnableEMABuffers_M15 || EnablePriceTrendFilter_M15 || EnableMomentumTrendFilter_M15)
                        baseIndex++;
                    if (EnableEMA34SR_M30 || EnableEMABuffers_M30 || EnablePriceTrendFilter_M30 || EnableMomentumTrendFilter_M30)
                        baseIndex++;
                    if (EnableEMA34SR_M60 || EnableEMABuffers_M60 || EnablePriceTrendFilter_M60 || EnableMomentumTrendFilter_M60)
                        baseIndex++;
                    if (EnableEMA34SR_M240 || EnableEMABuffers_M240 || EnablePriceTrendFilter_M240 || EnableMomentumTrendFilter_M240)
                        baseIndex++;
                    return baseIndex;
                case "Weekly":
                    if (EnableEMA34SR_M15 || EnableEMABuffers_M15 || EnablePriceTrendFilter_M15 || EnableMomentumTrendFilter_M15)
                        baseIndex++;
                    if (EnableEMA34SR_M30 || EnableEMABuffers_M30 || EnablePriceTrendFilter_M30 || EnableMomentumTrendFilter_M30)
                        baseIndex++;
                    if (EnableEMA34SR_M60 || EnableEMABuffers_M60 || EnablePriceTrendFilter_M60 || EnableMomentumTrendFilter_M60)
                        baseIndex++;
                    if (EnableEMA34SR_M240 || EnableEMABuffers_M240 || EnablePriceTrendFilter_M240 || EnableMomentumTrendFilter_M240)
                        baseIndex++;
                    if (EnableEMA34SR_Daily || EnableEMABuffers_Daily || EnablePriceTrendFilter_Daily || EnableMomentumTrendFilter_Daily)
                        baseIndex++;
                    return baseIndex;
                default:
                    return baseIndex;
            }
        }

        private void CalculateSMACycles()
        {
            try
            {
                // Ensure we have enough data for both SMA and Stochastic
                if (CurrentBar < Math.Max(SMACyclePeriod + 1, 5 + 3))
                    return;

                // Use sophisticated SMA direction tracking (MomentumAgeIndicator-style)
                UpdateSMADirectionTracking();

                // Convert enum to string for backward compatibility with existing code
                if (lastNonFlatSMADirection == SMADirection.Up)
                    currentSMADirection = "Up";
                else if (lastNonFlatSMADirection == SMADirection.Down)
                    currentSMADirection = "Down";
                else
                    currentSMADirection = "Unknown";

                // Check if SMA direction changed - reset bounce counter
                if (currentSMADirection != "Unknown" && currentSMADirection != lastTrackedSMADirection && lastTrackedSMADirection != "Unknown")
                {
                    bouncesInCurrentSMADirection = 0;
                    lastBounceCountedBar = -1; // Reset bar tracking
                    lastCountedExtremeHigh = 0; // Reset extreme tracking
                    lastCountedExtremeLow = 0; // Reset extreme tracking
                    closedAboveLastExtremeHigh = false; // Reset close tracking
                    closedBelowLastExtremeLow = false; // Reset close tracking
                    if (ShowDebugInfo)
                        Print($"🔄 SMA Direction changed: {lastTrackedSMADirection} → {currentSMADirection} - Bounce counter and extreme tracking reset");
                }
                lastTrackedSMADirection = currentSMADirection;

                // Calculate current stochastic direction
                double currentStochK = stochM1.K[0];
                double previousStochK = stochM1.K[1];
                string currentStochDirection = "Unknown";

                if (currentStochK > previousStochK)
                    currentStochDirection = "Up";
                else if (currentStochK < previousStochK)
                    currentStochDirection = "Down";
                else
                    currentStochDirection = previousStochDirection; // No change, keep previous direction

                // Check if stochastic direction changed while SMA direction is the same
                if (currentStochDirection != "Unknown" &&
                    currentStochDirection != previousStochDirection &&
                    previousStochDirection != "Unknown" &&
                    currentSMADirection != "Unknown")
                {
                    // Stochastic direction changed while SMA direction is stable
                    smaCycleCount++;

                    if (ShowDebugInfo)
                        Print($"📊 Stochastic Cycle #{smaCycleCount} - SMA: {currentSMADirection}, Stoch: {previousStochDirection} → {currentStochDirection}");
                }

                // Update previous stochastic direction for next calculation
                if (currentStochDirection != "Unknown")
                    previousStochDirection = currentStochDirection;

                if (ShowDebugInfo && CurrentBar % 20 == 0) // Debug every 20 bars to avoid spam
                {
                    Print($"🔍 SMA Cycles: Direction={currentSMADirection}, Cycles={smaCycleCount}, StochK={currentStochK:F2}");
                }
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error calculating SMA cycles: {ex.Message}");
            }
        }

        private void UpdateSMADirectionTracking()
        {
            // Need at least 2 bars to detect direction
            if (CurrentBar < 2 || CurrentBar < SMACyclePeriod)
                return;

            // Calculate SMA slope (current vs previous)
            double currentSMA = smaCycle[0];
            double previousSMA = smaCycle[1];
            double slope = currentSMA - previousSMA;

            // Determine current direction (ignoring flat within threshold)
            SMADirection currentBarDirection = SMADirection.Unknown;
            if (Math.Abs(slope) > SMAFlatThreshold)
            {
                currentBarDirection = slope > 0 ? SMADirection.Up : SMADirection.Down;
            }

            // Only process if we have a non-flat direction for this bar
            if (currentBarDirection != SMADirection.Unknown)
            {
                // If this is the first direction we've established, set it as confirmed
                if (lastNonFlatSMADirection == SMADirection.Unknown)
                {
                    lastNonFlatSMADirection = currentBarDirection;
                    pendingDirection = SMADirection.Unknown;
                    consecutiveBarsInPendingDirection = 0;
                    if (ShowDebugInfo)
                        Print($"*** INITIAL SMA DIRECTION SET: {lastNonFlatSMADirection} at {Time[0]:HH:mm:ss}");
                    return;
                }

                // Check if current bar matches our established direction
                if (currentBarDirection == lastNonFlatSMADirection)
                {
                    // Reset any pending direction tracking - we're continuing in established direction
                    pendingDirection = SMADirection.Unknown;
                    consecutiveBarsInPendingDirection = 0;
                }
                else
                {
                    // Current bar is opposite to established direction
                    if (pendingDirection == currentBarDirection)
                    {
                        // Continuing in the pending (opposite) direction
                        consecutiveBarsInPendingDirection++;
                        if (ShowDebugInfo)
                            Print($"Consecutive bar #{consecutiveBarsInPendingDirection} in {currentBarDirection} direction (need {SMADirectionConsecutiveBars})");

                        // Check if we have enough consecutive bars to confirm direction change
                        if (consecutiveBarsInPendingDirection >= SMADirectionConsecutiveBars)
                        {
                            // Direction change confirmed!
                            SMADirection oldDirection = lastNonFlatSMADirection;
                            lastNonFlatSMADirection = currentBarDirection;

                            // Reset cycle counter (same as MomentumAgeIndicator line 316)
                            smaCycleCount = 0;

                            // Reset pending tracking
                            pendingDirection = SMADirection.Unknown;
                            consecutiveBarsInPendingDirection = 0;

                            if (ShowDebugInfo)
                            {
                                Print($"*** SMA DIRECTION CHANGE: {oldDirection} -> {lastNonFlatSMADirection} at {Time[0]:HH:mm:ss}");
                                Print($"    Required {SMADirectionConsecutiveBars} consecutive bars - CONFIRMED");
                                Print($"    Stochastic cycle counter RESET to 0");
                            }
                        }
                    }
                    else
                    {
                        // Starting a new pending direction
                        pendingDirection = currentBarDirection;
                        consecutiveBarsInPendingDirection = 1;
                        if (ShowDebugInfo)
                            Print($"Starting consecutive tracking: Bar 1 in {currentBarDirection} direction (need {SMADirectionConsecutiveBars})");
                    }
                }
            }
            else
            {
                // Current bar is flat - ignore it completely, don't affect consecutive counting
                if (ShowDebugInfo)
                    Print($"SMA flat bar ignored - slope={slope:F4}, threshold={SMAFlatThreshold}");
            }
        }

        private (string Direction, int TrendNumber, bool IsValid) GetDirectPriceTrendData(string timeframe)
        {
            switch (timeframe)
            {
                case "Minute3":
                    return (priceTrend_M3, priceTrendNumber_M3, priceTrend_M3 != "Unknown");
                case "Minute9":
                    return (priceTrend_M9, priceTrendNumber_M9, priceTrend_M9 != "Unknown");
                case "Minute15":
                    return (priceTrend_M15, priceTrendNumber_M15, priceTrend_M15 != "Unknown");
                case "Minute30":
                    return (priceTrend_M30, priceTrendNumber_M30, priceTrend_M30 != "Unknown");
                case "Minute60":
                    return (priceTrend_M60, priceTrendNumber_M60, priceTrend_M60 != "Unknown");
                case "Minute240":
                    return (priceTrend_M240, priceTrendNumber_M240, priceTrend_M240 != "Unknown");
                case "Day1":
                    return (priceTrend_Daily, priceTrendNumber_Daily, priceTrend_Daily != "Unknown");
                case "Week1":
                    return (priceTrend_Weekly, priceTrendNumber_Weekly, priceTrend_Weekly != "Unknown");
                default:
                    return ("Unknown", 0, false);
            }
        }

        private (string Direction, int TrendNumber, bool IsValid) GetDirectMACDTrendData(string timeframe)
        {
            switch (timeframe)
            {
                case "Minute3":
                    return (macdTrend_M3, macdTrendNumber_M3, macdTrend_M3 != "Unknown");
                case "Minute9":
                    return (macdTrend_M9, macdTrendNumber_M9, macdTrend_M9 != "Unknown");
                case "Minute15":
                    return (macdTrend_M15, macdTrendNumber_M15, macdTrend_M15 != "Unknown");
                case "Minute30":
                    return (macdTrend_M30, macdTrendNumber_M30, macdTrend_M30 != "Unknown");
                case "Minute60":
                    return (macdTrend_M60, macdTrendNumber_M60, macdTrend_M60 != "Unknown");
                case "Minute240":
                    return (macdTrend_M240, macdTrendNumber_M240, macdTrend_M240 != "Unknown");
                case "Day1":
                    return (macdTrend_Daily, macdTrendNumber_Daily, macdTrend_Daily != "Unknown");
                case "Week1":
                    return (macdTrend_Weekly, macdTrendNumber_Weekly, macdTrend_Weekly != "Unknown");
                default:
                    return ("Unknown", 0, false);
            }
        }

        private (string Direction, int CycleCount, bool IsValid) GetDirectSMACycleData()
        {
            // Return the current SMA cycle data calculated directly
            return (currentSMADirection, smaCycleCount, currentSMADirection != "Unknown");
        }

        private (string Direction, int MoveCount, bool IsValid) GetDirectChartMACDMoveData()
        {
            var macdDirection = chartMACDTrendState.currentTrend == MomentumTrend.Up ? "Up" :
                               chartMACDTrendState.currentTrend == MomentumTrend.Down ? "Down" : "Unknown";

            return (macdDirection, chartMACDTrendState.trendMovesSinceDirectionChange, macdDirection != "Unknown");
        }

        private (string Direction, int CycleCount, bool IsValid) ReadSMACycleData()
        {
            try
            {
                string timeframe = GetTimeFrameString(BarsPeriod);
                string folderPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8",
                    "SMA Cycle Data"
                );

                string filePath = Path.Combine(folderPath, $"{instrumentName}_{timeframe}_SMA{SMACyclePeriod}_CycleData.txt");

                if (!File.Exists(filePath))
                {
                    if (ShowDebugInfo)
                        Print($"❌ SMA Cycle file not found: {Path.GetFileName(filePath)}");
                    return ("Unknown", 0, false);
                }

                // Read the last line (most recent data)
                string[] lines = File.ReadAllLines(filePath);
                if (lines.Length == 0)
                {
                    if (ShowDebugInfo)
                        Print($"❌ SMA Cycle file is empty: {Path.GetFileName(filePath)}");
                    return ("Unknown", 0, false);
                }

                string lastLine = lines[lines.Length - 1];
                string[] parts = lastLine.Split('\t');

                if (parts.Length >= 4)
                {
                    string direction = parts[1];
                    if (int.TryParse(parts[2], out int cycleNumber))
                    {
                        return (direction, cycleNumber, true);
                    }
                }

                if (ShowDebugInfo)
                    Print($"❌ Invalid SMA Cycle data format in last line: {lastLine}");
                return ("Unknown", 0, false);
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error reading SMA Cycle data: {ex.Message}");
                return ("Unknown", 0, false);
            }
        }

        #endregion

        #region File Reading Methods - COMMENTED OUT (replaced with direct calculation)

        // OLD FILE READING LOGIC - COMMENTED OUT (replaced with direct calculation)
        /*
        private (string Direction, int TrendNumber, bool IsValid) ReadPriceTrendData(string timeframe)
        {
            try
            {
                string folderPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8",
                    "SMA Cycle Data"
                );
                
                string filePath = Path.Combine(folderPath, $"{instrumentName}_{timeframe}_SMA{SMACyclePeriod}_CycleData.txt");
                
                if (!File.Exists(filePath))
                {
                    if (ShowDebugInfo)
                        Print($"❌ Price Trend file not found: {Path.GetFileName(filePath)}");
                    return ("Unknown", 0, false);
                }
                
                // Read the last line (most recent data)
                string[] lines = File.ReadAllLines(filePath);
                if (lines.Length == 0)
                {
                    if (ShowDebugInfo)
                        Print($"❌ Price Trend file is empty: {Path.GetFileName(filePath)}");
                    return ("Unknown", 0, false);
                }
                
                string lastLine = lines[lines.Length - 1];
                string[] parts = lastLine.Split('\t');
                
                if (parts.Length >= 3)
                {
                    string timestamp = parts[0];
                    string direction = parts[1];
                    int cycleCount;
                    
                    if (int.TryParse(parts[2], out cycleCount))
                    {
                        if (ShowDebugInfo)
                            Print($"📊 Price Trend Data ({timeframe}): Direction={direction}, Cycles={cycleCount}, Timestamp={timestamp}");
                        return (direction, cycleCount, true);
                    }
                }
                
                if (ShowDebugInfo)
                    Print($"❌ Invalid Price Trend data format in last line: {lastLine}");
                return ("Unknown", 0, false);
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error reading Price Trend data: {ex.Message}");
                return ("Unknown", 0, false);
            }
        }
        
        private (string Direction, int TrendNumber, bool IsValid) ReadMACDTrendData(string timeframe)
        {
            try
            {
                string folderPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8",
                    "SMA Cycle Data"
                );
                
                string filePath = Path.Combine(folderPath, $"{instrumentName}_{timeframe}_SMA{SMACyclePeriod}_MACDTrend.txt");
                
                if (!File.Exists(filePath))
                {
                    if (ShowDebugInfo)
                        Print($"❌ MACD Trend file not found: {Path.GetFileName(filePath)}");
                    return ("Unknown", 0, false);
                }
                
                // Read the last line (most recent data)
                string[] lines = File.ReadAllLines(filePath);
                if (lines.Length == 0)
                {
                    if (ShowDebugInfo)
                        Print($"❌ MACD Trend file is empty: {Path.GetFileName(filePath)}");
                    return ("Unknown", 0, false);
                }
                
                string lastLine = lines[lines.Length - 1];
                string[] parts = lastLine.Split('\t');
                
                if (parts.Length >= 3)
                {
                    string timestamp = parts[0];
                    string direction = parts[1];
                    int trendNumber;
                    
                    if (int.TryParse(parts[2], out trendNumber))
                    {
                        if (ShowDebugInfo)
                            Print($"📊 MACD Trend Data ({timeframe}): Direction={direction}, TrendNumber={trendNumber}, Timestamp={timestamp}");
                        return (direction, trendNumber, true);
                    }
                }
                
                if (ShowDebugInfo)
                    Print($"❌ Invalid MACD Trend data format in last line: {lastLine}");
                return ("Unknown", 0, false);
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error reading MACD Trend data: {ex.Message}");
                return ("Unknown", 0, false);
            }
        }

        private (string Direction, int CycleCount, bool IsValid) ReadSMACycleData()
        {
            try
            {
                string timeframe = GetTimeFrameString(BarsPeriod);
                string folderPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8",
                    "SMA Cycle Data"
                );
                
                string filePath = Path.Combine(folderPath, $"{instrumentName}_{timeframe}_SMA{SMACyclePeriod}_CycleData.txt");
                
                if (!File.Exists(filePath))
                {
                    if (ShowDebugInfo)
                        Print($"❌ SMA Cycle file not found: {Path.GetFileName(filePath)}");
                    return ("Unknown", 0, false);
                }
                
                // Read the last line (most recent data)
                string[] lines = File.ReadAllLines(filePath);
                if (lines.Length == 0)
                {
                    if (ShowDebugInfo)
                        Print($"❌ SMA Cycle file is empty: {Path.GetFileName(filePath)}");
                    return ("Unknown", 0, false);
                }
                
                string lastLine = lines[lines.Length - 1];
                string[] parts = lastLine.Split('\t');
                
                if (parts.Length >= 3)
                {
                    string timestamp = parts[0];
                    string direction = parts[1];
                    int cycleCount;
                    
                    if (int.TryParse(parts[2], out cycleCount))
                    {
                        if (ShowDebugInfo)
                            Print($"📊 SMA Cycle Data: Direction={direction}, Cycles={cycleCount}, Timestamp={timestamp}");
                        return (direction, cycleCount, true);
                    }
                }
                
                if (ShowDebugInfo)
                    Print($"❌ Invalid SMA Cycle data format in last line: {lastLine}");
                return ("Unknown", 0, false);
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error reading SMA Cycle data: {ex.Message}");
                return ("Unknown", 0, false);
            }
        }
        */

        #endregion
        
        #region Stop Loss & Position Sizing Methods
        
        private double CalculateStopLossPrice(bool isLong)
        {
            try
            {
                if (ShowPositionSizing)
                {
                    Print($"{Time[0]:HH:mm:ss} [{(isLong ? "LONG" : "SHORT")}] 🎯 Calculating swing-based stop loss...");
                }

                double extremePrice = 0;
                double stopLossPrice = 0;
                double atrValue = atr[0];
                double buffer = atrValue * (StopLossBufferPercent / 100.0);
                
                if (isLong)
                {
                    // For LONG: Find the lowest low during the pullback and subtract buffer
                    extremePrice = FindLowestLowDuringPullback(Time[0]);
                    stopLossPrice = extremePrice - buffer;
                    
                    if (ShowPositionSizing)
                    {
                        Print($"{Time[0]:HH:mm:ss} [LONG] 🎯 Stop Loss Calculation:");
                        Print($"    Lowest Low during pullback: {Instrument.MasterInstrument.FormatPrice(extremePrice)}");
                        Print($"    ATR(14): {Instrument.MasterInstrument.FormatPrice(atrValue)}");
                        Print($"    Buffer ({StopLossBufferPercent}% of ATR): {Instrument.MasterInstrument.FormatPrice(buffer)}");
                        Print($"    Final Stop Loss: {Instrument.MasterInstrument.FormatPrice(extremePrice)} - {Instrument.MasterInstrument.FormatPrice(buffer)} = {Instrument.MasterInstrument.FormatPrice(stopLossPrice)}");
                    }
                }
                else
                {
                    // For SHORT: Find the highest high during the pullback and add buffer
                    extremePrice = FindHighestHighDuringPullback(Time[0]);
                    stopLossPrice = extremePrice + buffer;
                    
                    if (ShowPositionSizing)
                    {
                        Print($"{Time[0]:HH:mm:ss} [SHORT] 🎯 Stop Loss Calculation:");
                        Print($"    Highest High during pullback: {Instrument.MasterInstrument.FormatPrice(extremePrice)}");
                        Print($"    ATR(14): {Instrument.MasterInstrument.FormatPrice(atrValue)}");
                        Print($"    Buffer ({StopLossBufferPercent}% of ATR): {Instrument.MasterInstrument.FormatPrice(buffer)}");
                        Print($"    Final Stop Loss: {Instrument.MasterInstrument.FormatPrice(extremePrice)} + {Instrument.MasterInstrument.FormatPrice(buffer)} = {Instrument.MasterInstrument.FormatPrice(stopLossPrice)}");
                    }
                }
                
                return stopLossPrice;
            }
            catch (Exception ex)
            {
                if (ShowPositionSizing)
                    Print($"{Time[0]:HH:mm:ss} [{(isLong ? "LONG" : "SHORT")}] ❌ Error calculating stop loss: {ex.Message}");
                return 0;
            }
        }
        
        private double FindLowestLowDuringPullback(DateTime currentTime)
        {
            try
            {
                // Find the lowest low from pullback start to current time (matches TrendlineAfterPullbackWriter)
                DateTime pullbackStart = longPullbackStartTime;
                if (pullbackStart == DateTime.MinValue)
                {
                    if (ShowPositionSizing){
                        Print($"[{currentTime:yyyy-MM-dd HH:mm:ss}] [LONG] ⚠️ No pullback start time, using extreme price: {Instrument.MasterInstrument.FormatPrice(longExtremePrice)}");
                    }
                    return longExtremePrice;
                }
                
                double lowestLow = double.MaxValue;
                DateTime lowestTime = DateTime.MinValue;
                int barsChecked = 0;
                
                // Scan from current bar backwards to find the pullback period
                for (int barsBack = 0; barsBack <= CurrentBar && barsBack <= 200; barsBack++) // Limit to 200 bars max
                {
                    DateTime barTime = Time[barsBack];
                    
                    // Stop when we reach before the pullback start
                    if (barTime < pullbackStart)
                        break;
                        
                    double barLow = Low[barsBack];
                    barsChecked++;
                    
                    if (barLow < lowestLow)
                    {
                        lowestLow = barLow;
                        lowestTime = barTime;
                        
                        if (ShowPositionSizing){
                            Print($"[{currentTime:yyyy-MM-dd HH:mm:ss}] [LONG] 🔍 New lowest low: {Instrument.MasterInstrument.FormatPrice(lowestLow)} at {lowestTime:HH:mm}");
                        }
                    }
                }
                
                if (lowestLow == double.MaxValue)
                {
                    // Fallback to extreme price if no pullback bars found
                    lowestLow = longExtremePrice;
                    if (ShowPositionSizing){
                        Print($"[{currentTime:yyyy-MM-dd HH:mm:ss}] [LONG] ⚠️ No pullback bars found, using extreme: {Instrument.MasterInstrument.FormatPrice(lowestLow)}");
                    }
                }
                else
                {
                    if (ShowPositionSizing){
                        Print($"[{currentTime:yyyy-MM-dd HH:mm:ss}] [LONG] ✅ Pullback analysis: {barsChecked} bars checked, lowest low: {Instrument.MasterInstrument.FormatPrice(lowestLow)} at {lowestTime:HH:mm}");
                    }
                }
                
                return lowestLow;
            }
            catch (Exception ex)
            {
                if (ShowPositionSizing){
                    Print($"[{currentTime:yyyy-MM-dd HH:mm:ss}] [LONG] ❌ Error finding lowest low: {ex.Message}");
                }
                return longExtremePrice; // Fallback
            }
        }
        
        private double FindHighestHighDuringPullback(DateTime currentTime)
        {
            try
            {
                // Find the highest high from pullback start to current time (matches TrendlineAfterPullbackWriter)
                DateTime pullbackStart = shortPullbackStartTime;
                if (pullbackStart == DateTime.MinValue)
                {
                    if (ShowPositionSizing){
                        Print($"[{currentTime:yyyy-MM-dd HH:mm:ss}] [SHORT] ⚠️ No pullback start time, using extreme price: {Instrument.MasterInstrument.FormatPrice(shortExtremePrice)}");
                    }
                    return shortExtremePrice;
                }
                
                double highestHigh = double.MinValue;
                DateTime highestTime = DateTime.MinValue;
                int barsChecked = 0;
                
                // Scan from current bar backwards to find the pullback period
                for (int barsBack = 0; barsBack <= CurrentBar && barsBack <= 200; barsBack++) // Limit to 200 bars max
                {
                    DateTime barTime = Time[barsBack];
                    
                    // Stop when we reach before the pullback start
                    if (barTime < pullbackStart)
                        break;
                        
                    double barHigh = High[barsBack];
                    barsChecked++;
                    
                    if (barHigh > highestHigh)
                    {
                        highestHigh = barHigh;
                        highestTime = barTime;
                        
                        if (ShowPositionSizing){
                            Print($"[{currentTime:yyyy-MM-dd HH:mm:ss}] [SHORT] 🔍 New highest high: {Instrument.MasterInstrument.FormatPrice(highestHigh)} at {highestTime:HH:mm}");
                        }
                    }
                }
                
                if (highestHigh == double.MinValue)
                {
                    // Fallback to extreme price if no pullback bars found
                    highestHigh = shortExtremePrice;
                    if (ShowPositionSizing){
                        Print($"[{currentTime:yyyy-MM-dd HH:mm:ss}] [SHORT] ⚠️ No pullback bars found, using extreme: {Instrument.MasterInstrument.FormatPrice(highestHigh)}");
                    }
                }
                else
                {
                    if (ShowPositionSizing){
                        Print($"[{currentTime:yyyy-MM-dd HH:mm:ss}] [SHORT] ✅ Pullback analysis: {barsChecked} bars checked, highest high: {Instrument.MasterInstrument.FormatPrice(highestHigh)} at {highestTime:HH:mm}");
                    }
                }
                
                return highestHigh;
            }
            catch (Exception ex)
            {
                if (ShowPositionSizing){
                    Print($"[{currentTime:yyyy-MM-dd HH:mm:ss}] [SHORT] ❌ Error finding highest high: {ex.Message}");
                }
                return shortExtremePrice; // Fallback
            }
        }
        
        #endregion
        
        #region Position Sizing Methods
        
        private void CalculateLongPositionSizing(double entryPrice, double stopLoss)
        {
            stopLossPrice = stopLoss;
            
            if (ShowPositionSizing)
            {
                Print($"🔍 LONG Position Sizing for {Instrument.MasterInstrument.Name}:");
                Print($"    Entry Price: {Instrument.MasterInstrument.FormatPrice(entryPrice)}");
                Print($"    Stop Loss Price: {Instrument.MasterInstrument.FormatPrice(stopLossPrice)}");
                Print($"    Instrument Type: {Instrument.MasterInstrument.InstrumentType}");
                Print($"    Point Value: ${Instrument.MasterInstrument.FormatPrice(Instrument.MasterInstrument.PointValue)}");
                Print($"    Tick Size: {Instrument.MasterInstrument.TickSize:F5}");
            }
            
            // Calculate risk in price points
            double riskInPoints = entryPrice - stopLossPrice;
            
            if (riskInPoints <= 0)
            {
                calculatedPositionSize = 0;
                riskAmount = 0;
                targetPrice = 0;
                riskRewardRatio = 0;
                
                if (ShowPositionSizing)
                    Print($"    ❌ Invalid risk: {Instrument.MasterInstrument.FormatPrice(riskInPoints)} points");
                return;
            }
            
            // Get account information
            double accountSize = GetAccountSize();
            riskAmount = accountSize * (AccountRiskPercent / 100.0);
            
            if (ShowPositionSizing)
            {
                Print($"    Account Size: ${accountSize:F0}");
                Print($"    Risk Percentage: {AccountRiskPercent}%");
                Print($"    Dollar Risk Amount: ${riskAmount:F2}");
                Print($"    Risk in Points: {Instrument.MasterInstrument.FormatPrice(riskInPoints)}");
            }
            
            // Calculate NT8-ready position size based on instrument type
            calculatedPositionSize = CalculateInstrumentSpecificPositionSize(riskAmount, riskInPoints);
            
            // Calculate target based on risk/reward ratio
            double rewardInPoints = riskInPoints * RiskRewardRatio;
            targetPrice = entryPrice + rewardInPoints;
            riskRewardRatio = RiskRewardRatio;
            
            if (ShowPositionSizing)
            {
                Print($"✅ LONG Position Calculated:");
                Print($"    NT8 Position Size: {calculatedPositionSize:F0} {GetPositionSizeUnit()}");
                Print($"    Dollar Risk: ${riskAmount:F2}");
                Print($"    Target Price: {Instrument.MasterInstrument.FormatPrice(targetPrice)}");
                Print($"    Expected Profit: ${(rewardInPoints * Instrument.MasterInstrument.PointValue * calculatedPositionSize):F2}");
            }
        }
        
        private void CalculateShortPositionSizing(double entryPrice, double stopLoss)
        {
            stopLossPrice = stopLoss;
            
            if (ShowPositionSizing)
            {
                Print($"🔍 SHORT Position Sizing for {Instrument.MasterInstrument.Name}:");
                Print($"    Entry Price: {Instrument.MasterInstrument.FormatPrice(entryPrice)}");
                Print($"    Stop Loss Price: {Instrument.MasterInstrument.FormatPrice(stopLossPrice)}");
                Print($"    Instrument Type: {Instrument.MasterInstrument.InstrumentType}");
                Print($"    Point Value: ${Instrument.MasterInstrument.FormatPrice(Instrument.MasterInstrument.PointValue)}");
                Print($"    Tick Size: {Instrument.MasterInstrument.TickSize:F5}");
            }
            
            // Calculate risk in price points
            double riskInPoints = stopLossPrice - entryPrice;
            
            if (riskInPoints <= 0)
            {
                calculatedPositionSize = 0;
                riskAmount = 0;
                targetPrice = 0;
                riskRewardRatio = 0;
                
                if (ShowPositionSizing)
                    Print($"    ❌ Invalid risk: {Instrument.MasterInstrument.FormatPrice(riskInPoints)} points");
                return;
            }
            
            // Get account information
            double accountSize = GetAccountSize();
            riskAmount = accountSize * (AccountRiskPercent / 100.0);
            
            if (ShowPositionSizing)
            {
                Print($"    Account Size: ${accountSize:F0}");
                Print($"    Risk Percentage: {AccountRiskPercent}%");
                Print($"    Dollar Risk Amount: ${riskAmount:F2}");
                Print($"    Risk in Points: {Instrument.MasterInstrument.FormatPrice(riskInPoints)}");
            }
            
            // Calculate NT8-ready position size based on instrument type
            calculatedPositionSize = CalculateInstrumentSpecificPositionSize(riskAmount, riskInPoints);
            
            // Calculate target based on risk/reward ratio
            double rewardInPoints = riskInPoints * RiskRewardRatio;
            targetPrice = entryPrice - rewardInPoints;
            riskRewardRatio = RiskRewardRatio;
            
            if (ShowPositionSizing)
            {
                Print($"✅ SHORT Position Calculated:");
                Print($"    NT8 Position Size: {calculatedPositionSize:F0} {GetPositionSizeUnit()}");
                Print($"    Dollar Risk: ${riskAmount:F2}");
                Print($"    Target Price: {Instrument.MasterInstrument.FormatPrice(targetPrice)}");
                Print($"    Expected Profit: ${(rewardInPoints * Instrument.MasterInstrument.PointValue * calculatedPositionSize):F2}");
            }
        }
        
        #endregion
        
        #region Helper Methods
        
        private double CalculateInstrumentSpecificPositionSize(double dollarRisk, double riskInPoints)
        {
            try
            {
                double positionSize = 0;
                
                // Get instrument details
                string instrumentName = Instrument.MasterInstrument.Name;
                InstrumentType instrumentType = Instrument.MasterInstrument.InstrumentType;
                double pointValue = Instrument.MasterInstrument.PointValue;
                double tickSize = Instrument.MasterInstrument.TickSize;
                
                if (ShowPositionSizing)
                {
                    Print($"    🔧 Calculating position size:");
                    Print($"        Instrument: {instrumentName}");
                    Print($"        Type: {instrumentType}");
                    Print($"        Point Value: ${Instrument.MasterInstrument.FormatPrice(pointValue)}");
                    Print($"        Tick Size: {tickSize:F5}");
                }
                
                switch (instrumentType)
                {
                    case InstrumentType.Future:
                        // For futures: Position size = Dollar Risk ÷ (Risk in Points × Point Value)
                        positionSize = dollarRisk / (riskInPoints * pointValue);
                        
                        if (ShowPositionSizing)
                        {
                            Print($"        📊 FUTURES calculation:");
                            Print($"            ${dollarRisk:F2} ÷ ({Instrument.MasterInstrument.FormatPrice(riskInPoints)} × ${Instrument.MasterInstrument.FormatPrice(pointValue)})");
                            Print($"            = ${dollarRisk:F2} ÷ ${(riskInPoints * pointValue):F2}");
                            Print($"            = {positionSize:F0} contracts");
                        }
                        break;
                        
                    case InstrumentType.Stock:
                        // For stocks: Position size = Dollar Risk ÷ Risk per Share
                        // Point value for stocks is typically $1 per point
                        positionSize = dollarRisk / riskInPoints;
                        
                        if (ShowPositionSizing)
                        {
                            Print($"        📊 STOCK calculation:");
                            Print($"            ${dollarRisk:F2} ÷ ${Instrument.MasterInstrument.FormatPrice(riskInPoints)} risk per share");
                            Print($"            = {positionSize:F0} shares");
                        }
                        break;
                        
                    case InstrumentType.Forex:
                        // For forex: More complex due to pip values and lot sizes
                        positionSize = CalculateForexPositionSize(dollarRisk, riskInPoints, instrumentName);
                        break;
                        
                    case InstrumentType.Index:
                        // For index CFDs: Similar to futures but check point value
                        positionSize = dollarRisk / (riskInPoints * pointValue);
                        
                        if (ShowPositionSizing)
                        {
                            Print($"        📊 INDEX calculation:");
                            Print($"            ${dollarRisk:F2} ÷ ({Instrument.MasterInstrument.FormatPrice(riskInPoints)} × ${Instrument.MasterInstrument.FormatPrice(pointValue)})");
                            Print($"            = {positionSize:F0} units");
                        }
                        break;
                        
                    default:
                        // Generic calculation for other instrument types
                        if (pointValue > 0)
                        {
                            positionSize = dollarRisk / (riskInPoints * pointValue);
                        }
                        else
                        {
                            // Fallback: assume $1 per point like stocks
                            positionSize = dollarRisk / riskInPoints;
                        }
                        
                        if (ShowPositionSizing)
                        {
                            Print($"        📊 GENERIC calculation for {instrumentType}:");
                            Print($"            Position Size: {positionSize:F0}");
                        }
                        break;
                }
                
                // Round to appropriate precision and ensure minimum of 1
                positionSize = Math.Max(1, Math.Round(positionSize, 0));
                
                return positionSize;
            }
            catch (Exception ex)
            {
                if (ShowPositionSizing)
                    Print($"❌ Error calculating instrument-specific position size: {ex.Message}");
                return 1.0; // Return minimum safe position size
            }
        }
        
        private double CalculateForexPositionSize(double dollarRisk, double riskInPips, string instrumentName)
        {
            try
            {
                // Standard lot size for most forex pairs
                double standardLotSize = 100000;
                
                // Pip value calculation depends on the currency pair
                double pipValue = CalculateForexPipValue(instrumentName, standardLotSize);
                
                if (pipValue > 0)
                {
                    // Convert price points to pips (typically divide by 0.0001 for most pairs)
                    double riskInPipsActual = riskInPips / Instrument.MasterInstrument.TickSize;
                    
                    // Position size in standard lots
                    double positionInLots = dollarRisk / (riskInPipsActual * pipValue);
                    
                    // Convert to units (some brokers use units, others use lots)
                    double positionSize = positionInLots * standardLotSize;
                    
                    if (ShowPositionSizing)
                    {
                        Print($"        📊 FOREX calculation for {instrumentName}:");
                        Print($"            Risk in pips: {riskInPipsActual:F1}");
                        Print($"            Pip value: ${pipValue:F5}");
                        Print($"            Position in lots: {positionInLots:F2}");
                        Print($"            Position in units: {positionSize:F0}");
                    }
                    
                    return Math.Max(1000, Math.Round(positionSize, -3)); // Round to nearest 1000 units
                }
                else
                {
                    // Fallback for unknown forex pairs
                    return Math.Max(1000, Math.Round(dollarRisk / (riskInPips * 10), -3));
                }
            }
            catch (Exception ex)
            {
                if (ShowPositionSizing)
                    Print($"❌ Error calculating forex position size: {ex.Message}");
                return 1000.0; // Return minimum safe forex position
            }
        }
        
        private double CalculateForexPipValue(string instrumentName, double lotSize)
        {
            try
            {
                // This is a simplified pip value calculator
                // In a production system, you'd want to fetch current exchange rates
                
                // Most major pairs (EUR/USD, GBP/USD, etc.) where USD is quote currency
                if (instrumentName.EndsWith("USD"))
                {
                    return 10.0; // $10 per pip for standard lot
                }
                // USD as base currency (USD/JPY, USD/CHF, etc.)
                else if (instrumentName.StartsWith("USD"))
                {
                    return 10.0; // Approximate - would need current rate for exact calculation
                }
                // Cross pairs (EUR/GBP, EUR/JPY, etc.)
                else
                {
                    return 10.0; // Approximate - would need current rates
                }
                
                // Note: This is simplified. In production, pip values should be calculated using:
                // PipValue = (1 pip / Exchange Rate) * Lot Size
                // Where exchange rate is for the quote currency to account currency
            }
            catch (Exception ex)
            {
                if (ShowPositionSizing)
                    Print($"❌ Error calculating pip value for {instrumentName}: {ex.Message}");
                return 10.0; // Default approximation
            }
        }
        
        private string GetPositionSizeUnit()
        {
            switch (Instrument.MasterInstrument.InstrumentType)
            {
                case InstrumentType.Future:
                    return "contracts";
                case InstrumentType.Stock:
                    return "shares";
                case InstrumentType.Forex:
                    return "units";
                case InstrumentType.Index:
                    return "units";
                default:
                    return "units";
            }
        }
        
        private double GetAccountSize()
        {
            try
            {
                if (UseRealAccountSize)
                {
                    // Try to get real account size from NT8's account object
                    // Note: This is a simplified implementation
                    // In practice, you'd integrate with NT8's Account object
                    if (ShowPositionSizing)
                        Print($"⚠️ Real account size integration not yet implemented - using manual setting");
                }
                
                // Use manual account size setting
                if (ShowPositionSizing)
                    Print($"💰 Using manual account size: ${AccountSize:F2}");
                
                return AccountSize;
            }
            catch (Exception ex)
            {
                if (ShowPositionSizing)
                    Print($"❌ Error getting account size: {ex.Message}");
                return AccountSize; // Fallback to manual setting
            }
        }

        #endregion

        #region Advanced Cycle Tracking Methods

        // Update SMA direction tracking for a specific timeframe
        private void UpdateSMADirectionTracking(string timeframe, SMA smaIndicator, ATR atrIndicator)
        {
            if (smaIndicator == null || CurrentBar < 2) return;

            // ✅ FIX: Check if the specific timeframe has enough bars before accessing SMA data
            int timeframeBarIndex = GetBarsArrayIndex(timeframe);
            if (CurrentBars[timeframeBarIndex] < SMACyclePeriod + 2) return; // Need SMA period + previous bar

            // Calculate SMA slope using flat threshold
            double currentSMA = smaIndicator[0];
            double previousSMA = smaIndicator[1];
            double slope = currentSMA - previousSMA;
            double slopeAbs = Math.Abs(slope);

            SMADirection currentBarDirection = SMADirection.Unknown;

            // Only consider directions above flat threshold
            if (slopeAbs > SMAFlatThreshold)
            {
                currentBarDirection = slope > 0 ? SMADirection.Up : SMADirection.Down;
            }

            // Get current state for this timeframe
            var currentState = GetSMADirectionState(timeframe);

            if (currentBarDirection != SMADirection.Unknown)
            {
                // Initialize direction on first non-flat bar
                if (currentState.lastNonFlatDirection == SMADirection.Unknown)
                {
                    currentState.lastNonFlatDirection = currentBarDirection;
                    currentState.pendingDirection = SMADirection.Unknown;
                    currentState.consecutiveBarsInPendingDirection = 0;

                    if (ShowDebugInfo)
                        Print($"*** [{timeframe}] INITIAL SMA DIRECTION SET: {currentState.lastNonFlatDirection} at {Time[0]:HH:mm:ss}");

                    return;
                }

                // If current direction matches established direction, reset pending tracking
                if (currentBarDirection == currentState.lastNonFlatDirection)
                {
                    currentState.pendingDirection = SMADirection.Unknown;
                    currentState.consecutiveBarsInPendingDirection = 0;
                }
                else
                {
                    // Direction is opposite to current - track consecutive bars
                    if (currentState.pendingDirection == currentBarDirection)
                    {
                        // Continue tracking same pending direction
                        currentState.consecutiveBarsInPendingDirection++;

                        if (ShowDebugInfo)
                            Print($"[{timeframe}] Consecutive bar #{currentState.consecutiveBarsInPendingDirection} in {currentBarDirection} direction (need {SMADirectionConsecutiveBars})");

                        // Check if we have enough consecutive bars to confirm direction change
                        if (currentState.consecutiveBarsInPendingDirection >= SMADirectionConsecutiveBars)
                        {
                            // Direction change confirmed - reset cycle count
                            currentState.cyclesSinceDirectionChange = 0;
                            SMADirection oldDirection = currentState.lastNonFlatDirection;
                            currentState.lastNonFlatDirection = currentBarDirection;
                            currentState.lastDirectionChangeTime = Times[timeframeBarIndex][0];  // ✅ FIXED: Use correct timeframe time
                            currentState.consecutiveBarsInPendingDirection = 0;
                            currentState.pendingDirection = SMADirection.Unknown;

                            if (ShowDebugInfo)
                            {
                                Print($"*** [{timeframe}] SMA DIRECTION CHANGE: {oldDirection} -> {currentState.lastNonFlatDirection} at {Time[0]:HH:mm:ss}");
                                Print($"    Required {SMADirectionConsecutiveBars} consecutive bars - CONFIRMED");
                            }
                        }
                    }
                    else
                    {
                        // Start tracking new pending direction
                        currentState.pendingDirection = currentBarDirection;
                        currentState.consecutiveBarsInPendingDirection = 1;

                        if (ShowDebugInfo)
                            Print($"[{timeframe}] Starting consecutive tracking: Bar 1 in {currentBarDirection} direction (need {SMADirectionConsecutiveBars})");
                    }
                }
            }
            else
            {
                // Flat bar - don't advance consecutive tracking but don't reset it either
                if (ShowDebugInfo && currentState.pendingDirection != SMADirection.Unknown)
                    Print($"[{timeframe}] Flat bar - pending direction tracking paused at {currentState.consecutiveBarsInPendingDirection} bars");
            }
        }

        // Get or create SMA direction state for a timeframe
        private SMADirectionState GetSMADirectionState(string timeframe)
        {
            switch (timeframe)
            {
                case "Chart": return chartSMADirectionState;
                case "M3": return smaDirectionState_M3;
                case "M9": return smaDirectionState_M9;
                case "M15": return smaDirectionState_M15;
                case "M30": return smaDirectionState_M30;
                case "M60": return smaDirectionState_M60;
                case "M240": return smaDirectionState_M240;
                case "Daily": return smaDirectionState_Daily;
                case "Weekly": return smaDirectionState_Weekly;
                default: return smaDirectionState_M3; // Fallback
            }
        }

        // Check if a stochastic cycle is relevant for current SMA direction
        private bool IsRelevantCycleForSMADirection(string timeframe, bool isStochLow)
        {
            var state = GetSMADirectionState(timeframe);

            // Only count cycles that align with SMA direction
            if (state.lastNonFlatDirection == SMADirection.Up)
            {
                // For UP SMA, only count stochastic LOWS (rally cycles)
                return !isStochLow;
            }
            else if (state.lastNonFlatDirection == SMADirection.Down)
            {
                // For DOWN SMA, only count stochastic HIGHS (dip cycles)
                return isStochLow;
            }

            return false; // Unknown direction - don't count
        }

        // Validate price progression for cycle counting (using ATR buffer)
        private bool ValidatePriceProgression(string timeframe, bool isStochLow, double cyclePrice, ATR atrIndicator)
        {
            if (atrIndicator == null) return true; // Default to true if no ATR

            // ✅ FIX: Check if the specific timeframe has enough bars before accessing ATR data
            int timeframeBarIndex = GetBarsArrayIndex(timeframe);
            if (CurrentBars[timeframeBarIndex] < 14) return true; // ATR needs 14 bars, return true (allow progression) if not enough data

            var state = GetSMADirectionState(timeframe);
            double currentPrice = Closes[timeframeBarIndex][0];  // ✅ FIXED: Use correct timeframe price
            double atrValue = atrIndicator[0];
            double requiredProgressionBuffer = atrValue * (CycleProgressionBufferPercent / 100.0);

            if (state.lastNonFlatDirection == SMADirection.Up)
            {
                // For UP SMA, rallies should progress higher than previous cycle low
                if (!isStochLow && state.lastCyclePriceForProgression > 0)
                {
                    double requiredPrice = state.lastCyclePriceForProgression + requiredProgressionBuffer;
                    bool progressionValid = currentPrice > requiredPrice;

                    if (ShowDebugInfo)
                    {
                        Print($"[{timeframe}] UP SMA Rally Progression: Current={currentPrice:F2}, Required={requiredPrice:F2} (Last Low + {requiredProgressionBuffer:F2}), Valid={progressionValid}");
                    }

                    return progressionValid;
                }
            }
            else if (state.lastNonFlatDirection == SMADirection.Down)
            {
                // For DOWN SMA, dips should progress lower than previous cycle high
                if (isStochLow && state.lastCyclePriceForProgression > 0)
                {
                    double requiredPrice = state.lastCyclePriceForProgression - requiredProgressionBuffer;
                    bool progressionValid = currentPrice < requiredPrice;

                    if (ShowDebugInfo)
                    {
                        Print($"[{timeframe}] DOWN SMA Dip Progression: Current={currentPrice:F2}, Required={requiredPrice:F2} (Last High - {requiredProgressionBuffer:F2}), Valid={progressionValid}");
                    }

                    return progressionValid;
                }
            }

            // First cycle or non-relevant direction - always valid
            return true;
        }

        // Update stochastic cycle detection for a specific timeframe
        private void UpdateStochasticCycleDetection(string timeframe, Stochastics stochIndicator, ATR atrIndicator)
        {
            if (stochIndicator == null || CurrentBar < 2) return;

            // ✅ FIX: Check if the specific timeframe has enough bars before accessing indicator data
            int timeframeBarIndex = GetBarsArrayIndex(timeframe);
            if (CurrentBars[timeframeBarIndex] < 10) return; // Need at least 10 bars for stochastic calculation

            double stochDValue = stochIndicator.D[0];
            var state = GetStochasticCycleState(timeframe);

            // 🔍 STRATEGIC DEBUG: M3 stochastic cycle timing investigation with exact values
            if (ShowStochasticCycleDebug && timeframe == "M3")
            {
                double stochKValue = stochIndicator.K[0];
                Print($"🟩 STOCH-DEBUG [{timeframe}] {Time[0]:HH:mm:ss} Bar {CurrentBar}: StochK={stochKValue:F1}, StochD={stochDValue:F1}, State={state.currentState}, Looking={state.lookingForLow}, Extreme={state.extremeValue:F1}@{state.extremeBar}");

                // Also show high/low/close for context
                if (timeframeBarIndex < BarsArray.Length && CurrentBars[timeframeBarIndex] >= 0)
                {
                    double high = Highs[timeframeBarIndex][0];
                    double low = Lows[timeframeBarIndex][0];
                    double close = Closes[timeframeBarIndex][0];
                    Print($"🟦 STOCH-DEBUG [{timeframe}] {Time[0]:HH:mm:ss} M3 OHLC: H={high:F2}, L={low:F2}, C={close:F2}");
                }
            }

            if (ShowDebugInfo && (timeframe == "Chart" || timeframe == "M1")) // Debug for Chart and M1
                Print($"[{timeframe}] Bar {CurrentBar}: StochD={stochDValue:F1}, State={state.currentState}, Looking={state.lookingForLow}, Extreme={state.extremeValue:F1}@{state.extremeBar}");

            switch (state.currentState)
            {
                case CycleState.WaitingForCross:
                    if (stochDValue < 50.0)
                    {
                        state.currentState = CycleState.BelowFifty;
                        state.lookingForLow = true;
                        state.extremeValue = stochDValue;
                        state.extremeBar = CurrentBar;
                        state.extremeTime = Times[timeframeBarIndex][0];  // ✅ FIXED: Use correct timeframe time
                        state.cycleStartTime = Times[timeframeBarIndex][0];  // ✅ FIXED: Use correct timeframe time

                        if (ShowDebugInfo)
                            Print($"[{timeframe}] Stoch crossed below 50 - now BELOW, looking for LOW. Extreme set to {state.extremeValue:F1}@{CurrentBar}");
                    }
                    else if (stochDValue > 50.0)
                    {
                        state.currentState = CycleState.AboveFifty;
                        state.lookingForLow = false;
                        state.extremeValue = stochDValue;
                        state.extremeBar = CurrentBar;
                        state.extremeTime = Times[timeframeBarIndex][0];  // ✅ FIXED: Use correct timeframe time
                        state.cycleStartTime = Times[timeframeBarIndex][0];  // ✅ FIXED: Use correct timeframe time

                        if (ShowDebugInfo)
                            Print($"[{timeframe}] Stoch crossed above 50 - now ABOVE, looking for HIGH. Extreme set to {state.extremeValue:F1}@{CurrentBar}");
                    }
                    break;

                case CycleState.BelowFifty:
                    if (state.lookingForLow && stochDValue < state.extremeValue)
                    {
                        state.extremeValue = stochDValue;
                        state.extremeBar = CurrentBar;
                        state.extremeTime = Times[timeframeBarIndex][0];  // ✅ FIXED: Use correct timeframe time
                    }

                    if (stochDValue > 50.0)
                    {
                        if (ShowStochasticCycleDebug && timeframe == "M3")
                            Print($"🟠 STOCH-DEBUG [{timeframe}] {Time[0]:HH:mm:ss} *** CYCLE LOW COMPLETE! Extreme={state.extremeValue:F1}@{state.extremeBar}, StochD now={stochDValue:F1}");

                        if (ShowDebugInfo)
                            Print($"*** [{timeframe}] CYCLE LOW COMPLETE! Extreme={state.extremeValue:F1}@{state.extremeBar}, StochD now={stochDValue:F1}");

                        ProcessStochasticCycle(timeframe, true, state.extremeValue, state.extremeTime, state.extremeBar, atrIndicator);

                        state.currentState = CycleState.AboveFifty;
                        state.lookingForLow = false;
                        state.extremeValue = stochDValue;
                        state.extremeBar = CurrentBar;
                        state.extremeTime = Times[timeframeBarIndex][0];  // ✅ FIXED: Use correct timeframe time
                        state.cycleStartTime = Times[timeframeBarIndex][0];  // ✅ FIXED: Use correct timeframe time
                    }
                    break;

                case CycleState.AboveFifty:
                    if (!state.lookingForLow && stochDValue > state.extremeValue)
                    {
                        state.extremeValue = stochDValue;
                        state.extremeBar = CurrentBar;
                        state.extremeTime = Times[timeframeBarIndex][0];  // ✅ FIXED: Use correct timeframe time
                    }

                    if (stochDValue < 50.0)
                    {
                        if (ShowStochasticCycleDebug && timeframe == "M3")
                            Print($"🔴 STOCH-DEBUG [{timeframe}] {Time[0]:HH:mm:ss} *** CYCLE HIGH COMPLETE! Extreme={state.extremeValue:F1}@{state.extremeBar}, StochD now={stochDValue:F1}");

                        if (ShowDebugInfo)
                            Print($"*** [{timeframe}] CYCLE HIGH COMPLETE! Extreme={state.extremeValue:F1}@{state.extremeBar}, StochD now={stochDValue:F1}");

                        ProcessStochasticCycle(timeframe, false, state.extremeValue, state.extremeTime, state.extremeBar, atrIndicator);

                        state.currentState = CycleState.BelowFifty;
                        state.lookingForLow = true;
                        state.extremeValue = stochDValue;
                        state.extremeBar = CurrentBar;
                        state.extremeTime = Times[timeframeBarIndex][0];  // ✅ FIXED: Use correct timeframe time
                        state.cycleStartTime = Times[timeframeBarIndex][0];  // ✅ FIXED: Use correct timeframe time
                    }
                    break;
            }
        }

        // Process completed stochastic cycle
        private void ProcessStochasticCycle(string timeframe, bool isLow, double extremeValue, DateTime extremeTime, int extremeBar, ATR atrIndicator)
        {
            // ✅ FIX: Get correct timeframe index for price access
            int timeframeBarIndex = GetBarsArrayIndex(timeframe);

            var smaState = GetSMADirectionState(timeframe);
            var stochState = GetStochasticCycleState(timeframe);

            if (ShowDebugInfo)
                Print($"*** [{timeframe}] STOCHASTIC {(isLow ? "LOW" : "HIGH")} CYCLE COMPLETED");

            // Check if this cycle is relevant for current SMA direction
            bool isRelevant = IsRelevantCycleForSMADirection(timeframe, isLow);

            if (isRelevant && EnableCycleProgressionValidation)
            {
                // Validate price progression
                bool progressionValid = ValidatePriceProgression(timeframe, isLow, extremeValue, atrIndicator);

                if (progressionValid)
                {
                    smaState.cyclesSinceDirectionChange++;
                    smaState.lastCyclePriceForProgression = Closes[timeframeBarIndex][0]; // ✅ FIXED: Use correct timeframe price

                    // Show M3 price trend count box if enabled
                    if (timeframe == "M3")
                    {
                        string direction = smaState.lastNonFlatDirection == SMADirection.Up ? "Up" :
                                         smaState.lastNonFlatDirection == SMADirection.Down ? "Down" : "Unknown";
                        if (direction != "Unknown")
                        {
                            DrawM3PriceTrendCountBox(direction, smaState.cyclesSinceDirectionChange);
                        }
                    }

                    if (ShowDebugInfo)
                        Print($"    → [{timeframe}] RELEVANT for {smaState.lastNonFlatDirection} SMA: Cycle #{smaState.cyclesSinceDirectionChange} - PRICE PROGRESSION VALIDATED");
                }
                else
                {
                    if (ShowDebugInfo)
                        Print($"    → [{timeframe}] RELEVANT for {smaState.lastNonFlatDirection} SMA: No increment (stays at {smaState.cyclesSinceDirectionChange}) - INSUFFICIENT PRICE PROGRESSION");
                }
            }
            else if (isRelevant)
            {
                // No progression validation - just count the cycle
                smaState.cyclesSinceDirectionChange++;
                smaState.lastCyclePriceForProgression = Closes[timeframeBarIndex][0]; // ✅ FIXED: Use correct timeframe price

                // Show M3 price trend count box if enabled
                if (timeframe == "M3")
                {
                    string direction = smaState.lastNonFlatDirection == SMADirection.Up ? "Up" :
                                     smaState.lastNonFlatDirection == SMADirection.Down ? "Down" : "Unknown";
                    if (direction != "Unknown")
                    {
                        DrawM3PriceTrendCountBox(direction, smaState.cyclesSinceDirectionChange);
                    }
                }

                if (ShowDebugInfo)
                    Print($"    → [{timeframe}] RELEVANT for {smaState.lastNonFlatDirection} SMA: Cycle #{smaState.cyclesSinceDirectionChange} - NO PROGRESSION VALIDATION");
            }
            else
            {
                if (ShowDebugInfo)
                    Print($"    → [{timeframe}] NOT RELEVANT for {smaState.lastNonFlatDirection} SMA direction - ignored");
            }

            // ✅ CRITICAL: Process MACD for ALL cycles (like MomentumAgeIndicator line 611)
            // This happens REGARDLESS of stochastic cycle relevance
            DateTime cycleStart = stochState.cycleStartTime;
            DateTime cycleEnd = Time[0]; // Current bar time

            // Process MACD trend direction and move counting (like MomentumAgeIndicator)
            ProcessMACDTrendAtCyclePoint(timeframe, isLow, extremeValue, extremeTime, extremeBar, cycleStart, cycleEnd);
        }

        // Get stochastic cycle state for a timeframe
        private StochasticCycleState GetStochasticCycleState(string timeframe)
        {
            switch (timeframe)
            {
                case "Chart": return chartStochCycleState;
                case "M3": return stochCycleState_M3;
                case "M9": return stochCycleState_M9;
                case "M15": return stochCycleState_M15;
                case "M30": return stochCycleState_M30;
                case "M60": return stochCycleState_M60;
                case "M240": return stochCycleState_M240;
                case "Daily": return stochCycleState_Daily;
                case "Weekly": return stochCycleState_Weekly;
                default: return stochCycleState_M3; // Fallback
            }
        }

        // Process MACD trend at stochastic cycle point (EXACT MomentumAgeIndicator replication)
        private void ProcessMACDTrendAtCyclePoint(string timeframe, bool isLow, double stochValue, DateTime stochTime, int stochBar, DateTime cycleStart, DateTime cycleEnd)
        {
            var macdState = GetMACDTrendState(timeframe);
            var macdPoints = GetMACDPointsList(timeframe);

            // Find MACD extreme within cycle period (exact copy from MomentumAgeIndicator.AddCyclePoint)
            var macdExtreme = FindMacdExtremeInPeriod(timeframe, cycleStart, cycleEnd, !isLow); // !isLow because stoch low = macd high search

            if (ShowDebugInfo)
            {
                Print($"    → [{timeframe}] AddCyclePoint: Stoch extreme at {stochTime:HH:mm:ss}, Cycle period {cycleStart:HH:mm:ss} to {cycleEnd:HH:mm:ss}");
                Print($"    → [{timeframe}] MACD extreme found at {macdExtreme.Time:HH:mm:ss} with value {macdExtreme.Value:F4}");
            }

            // Create MACD point using found extreme (like MomentumAgeIndicator.AddCyclePoint)
            var macdPoint = new MACDPoint
            {
                Time = stochTime,                          // Stochastic extreme timestamp
                Value = macdExtreme.Value,                 // MACD extreme value
                BarIndex = macdExtreme.BarIndex,          // MACD extreme bar index
                Trend = MomentumTrend.Unknown,
                IsHigh = !isLow // Invert: stoch low = MACD high, stoch high = MACD low
            };

            macdPoints.Add(macdPoint);
            int currentIndex = macdPoints.Count - 1;

            // Update MACD trend state with cycle extremes (for breakout detection)
            if (macdPoint.IsHigh)
            {
                macdState.lastMACDHigh = macdExtreme.Value;
                if (ShowDebugInfo)
                    Print($"    → [{timeframe}] MACD HIGH updated: {macdExtreme.Value:F4}");
            }
            else
            {
                macdState.lastMACDLow = macdExtreme.Value;
                if (ShowDebugInfo)
                    Print($"    → [{timeframe}] MACD LOW updated: {macdExtreme.Value:F4}");
            }

            if (ShowDebugInfo)
                Print($"    → [{timeframe}] MACD Point added: {(macdPoint.IsHigh ? "HIGH" : "LOW")}, Value={macdExtreme.Value:F4}");

            if (ShowDebugInfo && timeframe == "Chart")
                Print($"🔍 CHART MACD: Adding point at {stochTime:HH:mm:ss}, MACD extreme={macdExtreme.Value:F4}, Total points={macdPoints.Count}");

            // Process this point immediately (like MomentumAgeIndicator.ProcessSingleCyclePoint)
            ProcessSingleMACDPoint(timeframe, macdPoint, currentIndex);

            // Keep only recent points to avoid memory issues
            if (macdPoints.Count > 50)
                macdPoints.RemoveAt(0);
        }

        // Process single MACD point (EXACT copy of MomentumAgeIndicator.ProcessSingleCyclePoint logic)
        private void ProcessSingleMACDPoint(string timeframe, MACDPoint current, int index)
        {
            var macdState = GetMACDTrendState(timeframe);
            var macdPoints = GetMACDPointsList(timeframe);

            if (ShowDebugInfo)
                Print($"    → [{timeframe}] ProcessSingleMACDPoint: Index={index}, IsHigh={current.IsHigh}, MACD={current.Value:F4}");

            if (index == 0)
            {
                // First point - start uptrend (exact MomentumAgeIndicator logic)
                macdState.currentTrend = MomentumTrend.Up;
                macdState.trendMovesSinceDirectionChange = 1;
                current.Trend = macdState.currentTrend;

                if (ShowDebugInfo)
                    Print($"    → [{timeframe}] First MACD point: Starting UPTREND, Number=1");
                return;
            }

            bool trendChanged = false;

            if (macdState.currentTrend == MomentumTrend.Up)
            {
                // In uptrend, check for trend change (exact MomentumAgeIndicator logic)
                if (!current.IsHigh)
                {
                    // Current is a low - check for lower low (breaks trend)
                    double lastLowValue = GetPreviousMACDLowValue(macdPoints, index);
                    if (ShowDebugInfo)
                        Print($"    → [{timeframe}] UPTREND Low check: Current={current.Value:F4}, LastLow={lastLowValue:F4}");

                    if (lastLowValue != double.MaxValue && current.Value < lastLowValue)
                    {
                        // Lower low - trend changes to down immediately
                        macdState.currentTrend = MomentumTrend.Down;
                        macdState.trendMovesSinceDirectionChange = 1;
                        trendChanged = true;

                        if (ShowDebugInfo)
                            Print($"    → [{timeframe}] MACD TREND CHANGE: Lower low detected, switching to DOWNTREND");
                    }
                }
                else
                {
                    // Current is a high - check for lower high (breaks trend)
                    double lastHighValue = GetPreviousMACDHighValue(macdPoints, index);

                    if (ShowStochasticCycleDebug && timeframe == "M3")
                        Print($"🟡 STOCH-DEBUG [{timeframe}] UPTREND High check: Current={current.Value:F4}, LastHigh={lastHighValue:F4} - {(current.Value < lastHighValue ? "LOWER HIGH!" : "HIGHER HIGH")}");

                    if (ShowDebugInfo)
                        Print($"    → [{timeframe}] UPTREND High check: Current={current.Value:F4}, LastHigh={lastHighValue:F4}");

                    if (lastHighValue != double.MinValue && current.Value < lastHighValue)
                    {
                        // Lower high - triggers retroactive relabeling (exact MomentumAgeIndicator logic)
                        if (ShowStochasticCycleDebug && timeframe == "M3")
                            Print($"🚨 STOCH-DEBUG [{timeframe}] RETROACTIVE: Lower high detected! Current={current.Value:F4} < LastHigh={lastHighValue:F4} - SWITCHING TO DOWNTREND");

                        if (ShowDebugInfo)
                            Print($"    → [{timeframe}] RETROACTIVE: Lower high detected, need to relabel previous low as #1 downtrend");

                        RetroactivelyRelabelForDowntrend(timeframe, index);
                        macdState.currentTrend = MomentumTrend.Down;
                        macdState.trendMovesSinceDirectionChange = 2; // Current becomes #2
                        trendChanged = true;

                        if (ShowDebugInfo)
                            Print($"    → [{timeframe}] MACD TREND CHANGE: Lower high detected, switching to DOWNTREND");
                    }
                }
            }
            else // currentTrend == MomentumTrend.Down
            {
                // In downtrend, check for trend change (exact MomentumAgeIndicator logic)
                if (current.IsHigh)
                {
                    // Current is a high - check for higher high (breaks trend)
                    double lastHighValue = GetPreviousMACDHighValue(macdPoints, index);
                    if (ShowDebugInfo)
                        Print($"    → [{timeframe}] DOWNTREND High check: Current={current.Value:F4}, LastHigh={lastHighValue:F4}");

                    if (lastHighValue != double.MinValue && current.Value > lastHighValue)
                    {
                        // Higher high - trend changes to up immediately
                        macdState.currentTrend = MomentumTrend.Up;
                        macdState.trendMovesSinceDirectionChange = 1;
                        trendChanged = true;

                        if (ShowDebugInfo)
                            Print($"    → [{timeframe}] MACD TREND CHANGE: Higher high detected, switching to UPTREND");
                    }
                }
                else
                {
                    // Current is a low - check for higher low (breaks trend)
                    double lastLowValue = GetPreviousMACDLowValue(macdPoints, index);
                    if (ShowDebugInfo)
                        Print($"    → [{timeframe}] DOWNTREND Low check: Current={current.Value:F4}, LastLow={lastLowValue:F4}");

                    if (lastLowValue != double.MaxValue && current.Value > lastLowValue)
                    {
                        // Higher low - triggers retroactive relabeling (exact MomentumAgeIndicator logic)
                        if (ShowDebugInfo)
                            Print($"    → [{timeframe}] RETROACTIVE: Higher low detected, need to relabel previous high as #1 uptrend");

                        RetroactivelyRelabelForUptrend(timeframe, index);
                        macdState.currentTrend = MomentumTrend.Up;
                        macdState.trendMovesSinceDirectionChange = 2; // Current becomes #2
                        trendChanged = true;

                        if (ShowDebugInfo)
                            Print($"    → [{timeframe}] MACD TREND CHANGE: Higher low detected, switching to UPTREND");
                    }
                }
            }

            if (!trendChanged)
            {
                // Same trend - increment move count (exact MomentumAgeIndicator logic)
                macdState.trendMovesSinceDirectionChange++;
            }

            current.Trend = macdState.currentTrend;
            current.TrendNumber = macdState.trendMovesSinceDirectionChange;

            if (ShowDebugInfo)
                Print($"    → [{timeframe}] Final MACD assignment: Trend={macdState.currentTrend}, Number={macdState.trendMovesSinceDirectionChange}");

            // Draw MACD count boxes for enabled timeframes
            if (timeframe == "Chart")
            {
                string direction = macdState.currentTrend == MomentumTrend.Up ? "Up" :
                                 macdState.currentTrend == MomentumTrend.Down ? "Down" : "Unknown";
                if (direction != "Unknown")
                {
                    DrawMACDCountBox(direction, macdState.trendMovesSinceDirectionChange);
                }
            }
            else if (timeframe == "M3")
            {
                string direction = macdState.currentTrend == MomentumTrend.Up ? "Up" :
                                 macdState.currentTrend == MomentumTrend.Down ? "Down" : "Unknown";
                if (direction != "Unknown")
                {
                    DrawM3MACDCountBox(direction, macdState.trendMovesSinceDirectionChange);
                }
            }
        }

        // Helper methods for MACD high/low value lookup (like MomentumAgeIndicator)
        private double GetPreviousMACDHighValue(List<MACDPoint> macdPoints, int currentIndex)
        {
            double currentValue = macdPoints[currentIndex].Value;

            for (int i = currentIndex - 1; i >= 0; i--)
            {
                if (macdPoints[i].IsHigh && Math.Abs(macdPoints[i].Value - currentValue) > 0.0001)
                    return macdPoints[i].Value;
            }
            return double.MinValue;
        }

        private double GetPreviousMACDLowValue(List<MACDPoint> macdPoints, int currentIndex)
        {
            double currentValue = macdPoints[currentIndex].Value;

            for (int i = currentIndex - 1; i >= 0; i--)
            {
                if (!macdPoints[i].IsHigh && Math.Abs(macdPoints[i].Value - currentValue) > 0.0001)
                    return macdPoints[i].Value;
            }
            return double.MaxValue;
        }

        // Find MACD extreme within cycle period (exact copy from MomentumAgeIndicator)
        private (DateTime Time, double Value, int BarIndex) FindMacdExtremeInPeriod(string timeframe, DateTime startTime, DateTime endTime, bool findHigh)
        {
            var macdIndicator = GetMACDIndicator(timeframe);
            int barsArrayIndex = GetBarsArrayIndex(timeframe);

            double extremeValue = findHigh ? double.MinValue : double.MaxValue;
            DateTime extremeTime = startTime;
            int extremeBarIndex = 0;

            // Search through the time period for MACD extreme - FORWARD IN TIME (oldest to newest)
            int maxBarsToSearch = Math.Min(CurrentBars[barsArrayIndex], macdIndicator.Count);
            for (int barsAgo = maxBarsToSearch - 1; barsAgo >= 0; barsAgo--)
            {
                // Use the correct timeframe's time array, not M1 time
                DateTime barTime = Times[barsArrayIndex][barsAgo];

                // Check if this bar is within our cycle period
                if (barTime >= startTime && barTime <= endTime)
                {
                    double macdVal = macdIndicator[barsAgo];

                    // Debug: Show what we're examining
                    if (ShowStochasticCycleDebug && timeframe == "M3")
                        Print($"🔍 MACD-SEARCH [{timeframe}] Bar {barsAgo}: Time={barTime:HH:mm:ss}, MACD={macdVal:F4}, Period={startTime:HH:mm:ss}-{endTime:HH:mm:ss}");

                    if ((findHigh && macdVal > extremeValue) || (!findHigh && macdVal < extremeValue))
                    {
                        extremeValue = macdVal;
                        extremeTime = barTime;
                        extremeBarIndex = CurrentBars[barsArrayIndex] - barsAgo;

                        if (ShowStochasticCycleDebug && timeframe == "M3")
                            Print($"🎯 NEW EXTREME [{timeframe}] Bar {barsAgo}: Time={barTime:HH:mm:ss}, MACD={macdVal:F4} (was {extremeValue:F4})");
                    }
                }
            }

            if (ShowStochasticCycleDebug && timeframe == "M3")
                Print($"🔵 STOCH-DEBUG [{timeframe}] FindMacdExtreme: Period {startTime:HH:mm:ss}-{endTime:HH:mm:ss}, Looking for {(findHigh ? "HIGH" : "LOW")}, Found {extremeValue:F4} at {extremeTime:HH:mm:ss}");

            if (ShowDebugInfo)
                Print($"    → [{timeframe}] FindMacdExtreme: Period {startTime:HH:mm:ss}-{endTime:HH:mm:ss}, Looking for {(findHigh ? "HIGH" : "LOW")}, Found {extremeValue:F4} at {extremeTime:HH:mm:ss}");

            return (extremeTime, extremeValue, extremeBarIndex);
        }

        // Retroactively relabel for downtrend (exact copy from MomentumAgeIndicator)
        private void RetroactivelyRelabelForDowntrend(string timeframe, int currentIndex)
        {
            var macdPoints = GetMACDPointsList(timeframe);

            if (ShowDebugInfo)
                Print($"    → [{timeframe}] RetroactivelyRelabelForDowntrend: Starting from index {currentIndex}");

            // Find the last low point and relabel it as #1 downtrend
            for (int i = currentIndex - 1; i >= 0; i--)
            {
                if (!macdPoints[i].IsHigh) // Found a low
                {
                    macdPoints[i].Trend = MomentumTrend.Down;
                    macdPoints[i].TrendNumber = 1;

                    if (ShowDebugInfo)
                        Print($"    → [{timeframe}] Retroactively relabeled point at index {i} as Down #1");
                    break;
                }
            }
        }

        // Retroactively relabel for uptrend (exact copy from MomentumAgeIndicator)
        private void RetroactivelyRelabelForUptrend(string timeframe, int currentIndex)
        {
            var macdPoints = GetMACDPointsList(timeframe);

            if (ShowDebugInfo)
                Print($"    → [{timeframe}] RetroactivelyRelabelForUptrend: Starting from index {currentIndex}");

            // Find the last high point and relabel it as #1 uptrend
            for (int i = currentIndex - 1; i >= 0; i--)
            {
                if (macdPoints[i].IsHigh) // Found a high
                {
                    macdPoints[i].Trend = MomentumTrend.Up;
                    macdPoints[i].TrendNumber = 1;

                    if (ShowDebugInfo)
                        Print($"    → [{timeframe}] Retroactively relabeled point at index {i} as Up #1");
                    break;
                }
            }
        }

        // Get MACD indicator for a timeframe
        private MACD GetMACDIndicator(string timeframe)
        {
            switch (timeframe)
            {
                case "Chart": return chartMacd;
                case "M3": return macd_M3;
                case "M9": return macd_M9;
                case "M15": return macd_M15;
                case "M30": return macd_M30;
                case "M60": return macd_M60;
                case "M240": return macd_M240;
                case "Daily": return macd_Daily;
                case "Weekly": return macd_Weekly;
                default: return chartMacd; // Fallback
            }
        }

        // Update MACD trend move tracking for a specific timeframe (DEPRECATED - now handled by cycle points)
        private void UpdateMACDTrendMoveTracking(string timeframe, MACD macdIndicator)
        {
            if (macdIndicator == null || CurrentBar < 2) return;

            // ✅ FIX: Check if the specific timeframe has enough bars before accessing MACD data
            int timeframeBarIndex = GetBarsArrayIndex(timeframe);
            if (CurrentBars[timeframeBarIndex] < 50) return; // Need at least 50 bars for MACD calculation

            double currentMacd = macdIndicator[0];
            double previousMacd = macdIndicator[1];

            var trendState = GetMACDTrendState(timeframe);
            var macdPoints = GetMACDPointsList(timeframe);

            // Determine current momentum trend direction
            MomentumTrend newTrend = MomentumTrend.Unknown;

            // Initialize trend on first bar with clear direction
            if (trendState.currentTrend == MomentumTrend.Unknown)
            {
                if (currentMacd > 0 && previousMacd > 0)
                    newTrend = MomentumTrend.Up;
                else if (currentMacd < 0 && previousMacd < 0)
                    newTrend = MomentumTrend.Down;

                if (newTrend != MomentumTrend.Unknown)
                {
                    trendState.currentTrend = newTrend;
                    Print($"🔵 [{Time[0]:yyyy-MM-dd HH:mm}] [{timeframe}] MACD Initial trend set: {newTrend}, MACD={currentMacd:F4}");
                }
                return;
            }

            // Check for trend changes based on breaking stochastic cycle extremes
            bool trendChanged = false;

            if (trendState.currentTrend == MomentumTrend.Up)
            {
                // In uptrend: Check if MACD breaks below the last MACD low (from stochastic cycle)
                if (trendState.lastMACDLow != 0)
                {
                    double breakThreshold = trendState.lastMACDLow * (1.0 - MACDReversalThreshold);
                    if (currentMacd < breakThreshold)
                    {
                        trendState.currentTrend = MomentumTrend.Down;
                        trendChanged = true;
                        Print($"🔴 [{Time[0]:yyyy-MM-dd HH:mm}] [{timeframe}] MACD Trend: UP → DOWN (broke below cycle low={trendState.lastMACDLow:F4}, threshold={MACDReversalThreshold:F2}), MACD={currentMacd:F4}");
                    }
                }
            }
            else if (trendState.currentTrend == MomentumTrend.Down)
            {
                // In downtrend: Check if MACD breaks above the last MACD high (from stochastic cycle)
                if (trendState.lastMACDHigh != 0)
                {
                    double breakThreshold = trendState.lastMACDHigh * (1.0 + MACDReversalThreshold);
                    if (currentMacd > breakThreshold)
                    {
                        trendState.currentTrend = MomentumTrend.Up;
                        trendChanged = true;
                        Print($"🟢 [{Time[0]:yyyy-MM-dd HH:mm}] [{timeframe}] MACD Trend: DOWN → UP (broke above cycle high={trendState.lastMACDHigh:F4}, threshold={MACDReversalThreshold:F2}), MACD={currentMacd:F4}");
                    }
                }
            }

            // Apply correct MomentumAgeIndicator counting logic - DIRECTION CHANGE ONLY
            if (trendChanged)
            {
                // ✅ FIXED: Reset to 1 on trend direction change (matches MomentumAgeIndicator)
                trendState.trendMovesSinceDirectionChange = 1;
            }
            // ✅ FIXED: Do NOT increment here - only increment when stochastic cycles complete
            // (following exact MomentumAgeIndicator logic where trendNumber++ only happens in ProcessSingleCyclePoint)

            // Add MACD point for tracking (always add when processing, not just on trend change)
            var macdPoint = new MACDPoint
            {
                Time = Time[0],
                Value = currentMacd,
                BarIndex = CurrentBar,
                Trend = trendState.currentTrend
            };

            macdPoints.Add(macdPoint);

            if (ShowDebugInfo && trendChanged)
                Print($"[{timeframe}] MACD Trend Direction Change: {trendState.currentTrend} (Move #{trendState.trendMovesSinceDirectionChange}) at {Time[0]:HH:mm:ss}, Value={currentMacd:F4}");

            // Keep only recent points to avoid memory issues
            if (macdPoints.Count > 100)
                macdPoints.RemoveAt(0);
        }

        // Check for significant MACD reversal (to avoid counting minor fluctuations)
        private bool HasSignificantMACDReversal(string timeframe, MACD macdIndicator, bool lookingForLow)
        {
            // Look back several bars to confirm this is a significant reversal
            int lookbackBars = 5;
            if (CurrentBar < lookbackBars) return false;

            // ✅ FIX: Check if the specific timeframe has enough bars before accessing MACD data
            int timeframeBarIndex = GetBarsArrayIndex(timeframe);
            if (CurrentBars[timeframeBarIndex] < lookbackBars + 1) return false;

            double currentMacd = macdIndicator[0];
            double extremeValue = currentMacd;

            // Find the extreme value in the lookback period
            for (int i = 1; i <= lookbackBars; i++)
            {
                // ✅ FIX: Check specific timeframe bar count instead of main timeframe
                if (CurrentBars[timeframeBarIndex] >= i)
                {
                    double historicalMacd = macdIndicator[i];
                    if (lookingForLow)
                    {
                        if (historicalMacd < extremeValue)
                            extremeValue = historicalMacd;
                    }
                    else
                    {
                        if (historicalMacd > extremeValue)
                            extremeValue = historicalMacd;
                    }
                }
            }

            // Check if current value represents a significant reversal from extreme
            double threshold = Math.Abs(extremeValue) * MACDReversalThreshold; // User-configurable reversal threshold

            if (lookingForLow)
            {
                bool isSignificantHigherLow = currentMacd > extremeValue && (currentMacd - extremeValue) > threshold;
                return isSignificantHigherLow;
            }
            else
            {
                bool isSignificantLowerHigh = currentMacd < extremeValue && (extremeValue - currentMacd) > threshold;
                return isSignificantLowerHigh;
            }
        }

        // Get MACD trend state for a timeframe
        private MACDTrendState GetMACDTrendState(string timeframe)
        {
            switch (timeframe)
            {
                case "Chart": return chartMACDTrendState;
                case "M3": return macdTrendState_M3;
                case "M9": return macdTrendState_M9;
                case "M15": return macdTrendState_M15;
                case "M30": return macdTrendState_M30;
                case "M60": return macdTrendState_M60;
                case "M240": return macdTrendState_M240;
                case "Daily": return macdTrendState_Daily;
                case "Weekly": return macdTrendState_Weekly;
                default: return macdTrendState_M3; // Fallback
            }
        }

        // Get MACD points list for a timeframe
        private List<MACDPoint> GetMACDPointsList(string timeframe)
        {
            switch (timeframe)
            {
                case "Chart": return macdPointsChart;
                case "M3": return macdPoints_M3;
                case "M9": return macdPoints_M9;
                case "M15": return macdPoints_M15;
                case "M30": return macdPoints_M30;
                case "M60": return macdPoints_M60;
                case "M240": return macdPoints_M240;
                case "Daily": return macdPoints_Daily;
                case "Weekly": return macdPoints_Weekly;
                default: return macdPoints_M3; // Fallback
            }
        }

        // Draw MACD count change box on chart
        private void DrawMACDCountBox(string direction, int count)
        {
            if (!ShowMACDCounts) return;

            string boxText = $"{direction} {count}";
            string tagName = $"MACDCount_{CurrentBar}_{Time[0]:HHmm}";

            // Position box above current bar
            double yPosition = High[0] + (atr[0] * 0.8);

            // Use different colors for Up vs Down
            Brush textColor = direction.ToUpper() == "UP" ? Brushes.LimeGreen : Brushes.Red;
            Brush backgroundColor = Brushes.Black;

            // Draw text positioned at the specific bar where the count change occurred
            Draw.Text(this, tagName, boxText, 0, yPosition, textColor);

            if (ShowDebugInfo)
                Print($"📊 MACD Count Box: {boxText} at Bar {CurrentBar}, Time {Time[0]:HH:mm:ss}");
        }

        // Draw M3 MACD count change box on chart
        private void DrawM3MACDCountBox(string direction, int count)
        {
            if (!ShowM3MACDCounts) return;

            string boxText = $"M3 {direction} {count}";
            string tagName = $"M3MACDCount_{CurrentBar}_{Time[0]:HHmm}";

            // Position box below current bar (different from chart MACD)
            double yPosition = Low[0] - (atr[0] * 0.8);

            // Use different colors for Up vs Down, with blue background for M3
            Brush textColor = direction.ToUpper() == "UP" ? Brushes.LimeGreen : Brushes.Red;
            Brush backgroundColor = Brushes.DarkBlue;

            // Draw text positioned at the specific bar where the count change occurred
            Draw.Text(this, tagName, boxText, 0, yPosition, textColor);

            if (ShowDebugInfo)
                Print($"📊 M3 MACD Count Box: {boxText} at Bar {CurrentBar}, Time {Time[0]:HH:mm:ss}");
        }

        // Draw M3 price trend count change box on chart
        private void DrawM3PriceTrendCountBox(string direction, int count)
        {
            if (!ShowM3PriceTrendCounts) return;

            string boxText = $"M3 Price {direction} {count}";
            string tagName = $"M3PriceCount_{CurrentBar}_{Time[0]:HHmm}";

            // Position box above current bar (different from M3 MACD which is below)
            double yPosition = High[0] + (atr[0] * 0.8);

            // Use different colors for Up vs Down, with purple background for M3 price
            Brush textColor = direction.ToUpper() == "UP" ? Brushes.LimeGreen : Brushes.Red;
            Brush backgroundColor = Brushes.DarkMagenta;

            // Draw text positioned at the specific bar where the count change occurred
            Draw.Text(this, tagName, boxText, 0, yPosition, textColor);

            if (ShowDebugInfo)
                Print($"📊 M3 Price Count Box: {boxText} at Bar {CurrentBar}, Time {Time[0]:HH:mm:ss}");
        }

        #endregion

        // Advanced Cycle Tracking Data Structures (nested classes)

        // SMA Direction State for cycle tracking
        public class SMADirectionState
        {
            public SMADirection lastNonFlatDirection = SMADirection.Unknown;
            public SMADirection pendingDirection = SMADirection.Unknown;
            public int consecutiveBarsInPendingDirection = 0;
            public int cyclesSinceDirectionChange = 0;
            public DateTime lastDirectionChangeTime = DateTime.MinValue;
            public double lastCyclePriceForProgression = 0;
        }

        // Stochastic Cycle State for cycle detection
        public class StochasticCycleState
        {
            public CycleState currentState = CycleState.WaitingForCross;
            public bool lookingForLow = false;
            public double extremeValue = 0;
            public int extremeBar = 0;
            public DateTime extremeTime = DateTime.MinValue;
            public DateTime cycleStartTime = DateTime.MinValue;
        }

        // MACD Trend State for trend move tracking
        public class MACDTrendState
        {
            public MomentumTrend currentTrend = MomentumTrend.Unknown;
            public int trendMovesSinceDirectionChange = 0;
            public DateTime lastTrendChangeTime = DateTime.MinValue;
            public double lastMACDLow = 0; // Most recent MACD low from stochastic cycle
            public double lastMACDHigh = 0; // Most recent MACD high from stochastic cycle
        }


        // Support/Resistance level data structure
        public class SRLevel
        {
            public double Price { get; set; }
            public string Source { get; set; }
            public string TimeFrame { get; set; }
            public string Type { get; set; }
            public bool IsActive { get; set; }
            public DateTime LastUpdate { get; set; }

            public string GetDisplayName()
            {
                return $"{Source} {TimeFrame} {Type}";
            }

            // Helper methods for price comparison
            public bool IsIntrabarResistance(double currentPrice) => IsActive && currentPrice < Price;
            public bool IsIntrabarSupport(double currentPrice) => IsActive && currentPrice > Price;
        }
        
        // EMA34SR data storage
        private List<SRLevel> allSRLevels = new List<SRLevel>();
        
        // EMA Buffer zone storage
        private Dictionary<string, double> emaNoLongZoneStart = new Dictionary<string, double>();   // Top EMA level
        private Dictionary<string, double> emaNoLongZoneEnd = new Dictionary<string, double>();     // Top EMA + buffer
        private Dictionary<string, double> emaNoShortZoneStart = new Dictionary<string, double>();  // Bottom EMA level  
        private Dictionary<string, double> emaNoShortZoneEnd = new Dictionary<string, double>();    // Bottom EMA - buffer
        
        private void CollectEMA34SRLevels(double currentPrice)
        {
            try
            {
                if (!IsEMA34SREnabled())
                {
                    if (ShowDebugInfo)
                        Print($"⏭️ [EMA34SR] Skipped - disabled in settings");
                    return;
                }
                
                // Clear existing levels
                allSRLevels.Clear();
                
                int emaLevelsAdded = 0;
                
                if (ShowDebugInfo)
                    Print($"🔍 [EMA34SR] Calculating levels directly from EMA indicators");
                
                // Calculate levels for each enabled timeframe
                if (EnableEMA34SR_M15 && emaHigh_M15 != null && emaLow_M15 != null)
                {
                    emaLevelsAdded += AddDirectEMALevels("M15", emaHigh_M15[0], emaLow_M15[0], EnableEMABuffers_M15, currentPrice);
                }
                if (EnableEMA34SR_M30 && emaHigh_M30 != null && emaLow_M30 != null)
                {
                    emaLevelsAdded += AddDirectEMALevels("M30", emaHigh_M30[0], emaLow_M30[0], EnableEMABuffers_M30, currentPrice);
                }
                if (EnableEMA34SR_M60 && emaHigh_M60 != null && emaLow_M60 != null)
                {
                    emaLevelsAdded += AddDirectEMALevels("M60", emaHigh_M60[0], emaLow_M60[0], EnableEMABuffers_M60, currentPrice);
                }
                if (EnableEMA34SR_M240 && emaHigh_M240 != null && emaLow_M240 != null)
                {
                    emaLevelsAdded += AddDirectEMALevels("M240", emaHigh_M240[0], emaLow_M240[0], EnableEMABuffers_M240, currentPrice);
                }
                if (EnableEMA34SR_Daily && emaHigh_Daily != null && emaLow_Daily != null)
                {
                    emaLevelsAdded += AddDirectEMALevels("Daily", emaHigh_Daily[0], emaLow_Daily[0], EnableEMABuffers_Daily, currentPrice);
                }
                if (EnableEMA34SR_Weekly && emaHigh_Weekly != null && emaLow_Weekly != null)
                {
                    emaLevelsAdded += AddDirectEMALevels("Weekly", emaHigh_Weekly[0], emaLow_Weekly[0], EnableEMABuffers_Weekly, currentPrice);
                }
                
                if (ShowDebugInfo)
                    Print($"📊 [EMA34SR] Total EMA34 levels calculated: {emaLevelsAdded}");
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                {
                    Print($"❌ [EMA34SR] Error: {ex.Message}");
                }
            }
        }
        
        private int AddDirectEMALevels(string timeframe, double emaHigh, double emaLow, bool includeBuffers, double currentPrice)
        {
            try
            {
                int levelsAdded = 0;
                
                if (ShowDebugInfo)
                    Print($"🔍 [EMA34SR {timeframe}] EMA High: {Instrument.MasterInstrument.FormatPrice(emaHigh)}, EMA Low: {Instrument.MasterInstrument.FormatPrice(emaLow)}");
                
                // Add EMA High level (if relevant)
                if (IsLevelRelevant(emaHigh, currentPrice))
                {
                    var emaHighLevel = new SRLevel
                    {
                        Price = emaHigh,
                        Source = "EMA34",
                        TimeFrame = timeframe,
                        Type = "High",
                        IsActive = true,
                        LastUpdate = Time[0]
                    };
                    allSRLevels.Add(emaHighLevel);
                    levelsAdded++;
                    
                    if (ShowDebugInfo)
                        Print($"    ✅ Added EMA High: {Instrument.MasterInstrument.FormatPrice(emaHigh)}");
                }
                
                // Add EMA Low level (if relevant)
                if (IsLevelRelevant(emaLow, currentPrice))
                {
                    var emaLowLevel = new SRLevel
                    {
                        Price = emaLow,
                        Source = "EMA34",
                        TimeFrame = timeframe,
                        Type = "Low",
                        IsActive = true,
                        LastUpdate = Time[0]
                    };
                    allSRLevels.Add(emaLowLevel);
                    levelsAdded++;
                    
                    if (ShowDebugInfo)
                        Print($"    ✅ Added EMA Low: {Instrument.MasterInstrument.FormatPrice(emaLow)}");
                }
                
                // Add buffer zones if enabled
                if (includeBuffers)
                {
                    levelsAdded += AddBufferZones(timeframe, emaHigh, emaLow, currentPrice);
                }
                
                return levelsAdded;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ [EMA34SR {timeframe}] Error adding levels: {ex.Message}");
                return 0;
            }
        }
        
        private int AddBufferZones(string timeframe, double emaHigh, double emaLow, double currentPrice)
        {
            try
            {
                int levelsAdded = 0;
                
                // Calculate buffer zones using same logic as EMA34SRWriter (lines 241-244)
                double emaDistance = Math.Abs(emaHigh - emaLow);
                double bufferAmount = emaDistance * (EMABufferPercent / 100.0); // User-configurable buffer percentage
                double emaHighBuffer = emaHigh + bufferAmount;
                double emaLowBuffer = emaLow - bufferAmount;
                
                if (ShowDebugInfo)
                    Print($"🔍 [EMA34SR {timeframe}] Buffer zones - High: {Instrument.MasterInstrument.FormatPrice(emaHighBuffer)}, Low: {Instrument.MasterInstrument.FormatPrice(emaLowBuffer)} (Buffer: {bufferAmount:F5})");
                
                // Store buffer zones in dictionaries for EMA buffer filtering logic
                string tfKey = timeframe;
                emaNoLongZoneStart[tfKey] = emaHigh;        // Top EMA level
                emaNoLongZoneEnd[tfKey] = emaHighBuffer;    // Top EMA + buffer
                emaNoShortZoneStart[tfKey] = emaLow;        // Bottom EMA level  
                emaNoShortZoneEnd[tfKey] = emaLowBuffer;    // Bottom EMA - buffer
                
                // NOTE: Buffer levels are NOT added to allSRLevels collection for S/R filtering
                // They are only used for EMA buffer filtering (entry blocking in buffer zones)
                if (ShowDebugInfo)
                {
                    Print($"    📝 Stored buffer zones in dictionaries (not in S/R collection):");
                    Print($"       No-Long Zone: {Instrument.MasterInstrument.FormatPrice(emaHigh)} to {Instrument.MasterInstrument.FormatPrice(emaHighBuffer)}");
                    Print($"       No-Short Zone: {Instrument.MasterInstrument.FormatPrice(emaLowBuffer)} to {Instrument.MasterInstrument.FormatPrice(emaLow)}");
                }
                
                return 0; // Buffer zones don't count as S/R levels added
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ [EMA34SR {timeframe}] Error adding buffer zones: {ex.Message}");
                return 0;
            }
        }
        
        // OLD FILE READING LOGIC - COMMENTED OUT (replaced with direct calculation)
        /*
        private List<SRLevel> ReadEMA34SRFile(string filePath, double currentPrice)
        {
            var levels = new List<SRLevel>();
            
            try
            {
                string fileName = Path.GetFileName(filePath);
                string timeFrame = ExtractTimeFrameFromEMAFileName(fileName);
                
                if (ShowDebugInfo)
                {
                    Print($"🔍 [EMA34SR FILE] Reading: {fileName}");
                    Print($"    Extracted timeframe: {timeFrame}");
                    Print($"    Use EMA Buffers: {IsEMABuffersEnabled()}");
                }
                
                string[] lines = File.ReadAllLines(filePath);
                if (ShowDebugInfo)
                    Print($"    Total lines in file: {lines.Length}");
                
                // Buffer level variables
                double emaHigh = 0;
                double emaLow = 0;
                double emaHighBuffer = 0;
                double emaLowBuffer = 0;
                
                int lineNumber = 0;
                foreach (string line in lines)
                {
                    lineNumber++;
                    
                    // Look for EMA_High and EMA_Low lines
                    if (line.StartsWith("EMA_High:") || line.StartsWith("EMA_Low:"))
                    {
                        if (ShowDebugInfo)
                            Print($"    Line {lineNumber}: Found EMA level line: {line}");
                        
                        var parsedLevel = ParseEMA34SRLine(line, timeFrame);
                        if (parsedLevel != null)
                        {
                            bool isRelevant = IsLevelRelevant(parsedLevel.Price, currentPrice);
                            double distance = Math.Abs(parsedLevel.Price - currentPrice);
                            
                            if (ShowDebugInfo)
                            {
                                Print($"        Parsed: {parsedLevel.Type} @ {Instrument.MasterInstrument.FormatPrice(parsedLevel.Price)}");
                                Print($"        Distance: {Instrument.MasterInstrument.FormatPrice(distance)} (max: {Instrument.MasterInstrument.FormatPrice(MaxSRDistancePoints)})");
                                Print($"        Relevant: {isRelevant}");
                            }
                            
                            if (isRelevant)
                            {
                                levels.Add(parsedLevel);
                                if (ShowDebugInfo)
                                    Print($"        ✅ Added to collection");
                            }
                            else
                            {
                                if (ShowDebugInfo)
                                    Print($"        ❌ Rejected - too far from current price");
                            }
                            
                            // Store EMA levels for buffer zone calculation
                            if (line.StartsWith("EMA_High:"))
                                emaHigh = parsedLevel.Price;
                            else if (line.StartsWith("EMA_Low:"))
                                emaLow = parsedLevel.Price;
                        }
                        else
                        {
                            if (ShowDebugInfo)
                                Print($"        ❌ Failed to parse line");
                        }
                    }
                    // Look for EMA buffer lines
                    else if (line.StartsWith("EMA_High_Buffer:"))
                    {
                        var parts = line.Split('|');
                        if (parts.Length > 0)
                        {
                            double.TryParse(parts[0].Substring("EMA_High_Buffer:".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out emaHighBuffer);
                            if (ShowDebugInfo) Print($"    Found EMA_High_Buffer: {emaHighBuffer:F5}");
                        }
                    }
                    else if (line.StartsWith("EMA_Low_Buffer:"))
                    {
                        var parts = line.Split('|');
                        if (parts.Length > 0)
                        {
                            double.TryParse(parts[0].Substring("EMA_Low_Buffer:".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out emaLowBuffer);
                            if (ShowDebugInfo) Print($"    Found EMA_Low_Buffer: {emaLowBuffer:F5}");
                        }
                    }
                    else if (line.StartsWith("CurrentRestriction:") || line.StartsWith("LastUpdate:") || 
                             line.StartsWith("Instrument:") || line.StartsWith("TimeFrame:") ||
                             line.StartsWith("CurrentPrice:") || line.StartsWith("ATR:") ||
                             line.StartsWith("EMAPeriod:") || string.IsNullOrEmpty(line))
                    {
                        // Skip header lines (but don't log them all to reduce spam)
                        if (lineNumber <= 10 && ShowDebugInfo) // Only log first 10 header lines
                            Print($"    Line {lineNumber}: Header/Info line: {line}");
                    }
                    else if (!string.IsNullOrEmpty(line))
                    {
                        if (ShowDebugInfo)
                            Print($"    Line {lineNumber}: Unknown format: {line}");
                    }
                }
                
                // EMA Buffer Zone Calculation - check if this specific timeframe's buffers are enabled
                bool thisTimeframeBufferEnabled = false;
                if (timeFrame == "15MIN") thisTimeframeBufferEnabled = EnableEMABuffers_M15;
                else if (timeFrame == "30MIN") thisTimeframeBufferEnabled = EnableEMABuffers_M30;
                else if (timeFrame == "1HOUR") thisTimeframeBufferEnabled = EnableEMABuffers_M60;
                else if (timeFrame == "4HOUR") thisTimeframeBufferEnabled = EnableEMABuffers_M240;
                else if (timeFrame == "DAILY") thisTimeframeBufferEnabled = EnableEMABuffers_Daily;
                else if (timeFrame == "WEEKLY") thisTimeframeBufferEnabled = EnableEMABuffers_Weekly;
                
                if (thisTimeframeBufferEnabled && emaHigh > 0 && emaLow > 0 && emaHighBuffer > 0 && emaLowBuffer > 0)
                {
                    // No LONG zone: from Top EMA to Top EMA Buffer (already calculated in file)
                    emaNoLongZoneStart[timeFrame] = emaHigh;
                    emaNoLongZoneEnd[timeFrame] = emaHighBuffer;
                    
                    // No SHORT zone: from Bottom EMA Buffer to Bottom EMA (already calculated in file)
                    emaNoShortZoneStart[timeFrame] = emaLow;
                    emaNoShortZoneEnd[timeFrame] = emaLowBuffer;
                    
                    if (ShowDebugInfo)
                    {
                        Print($"📊 [EMA BUFFER ZONES] {timeFrame}:");
                        Print($"    No LONG zone: {Instrument.MasterInstrument.FormatPrice(emaHigh)} to {Instrument.MasterInstrument.FormatPrice(emaHighBuffer)}");
                        Print($"    No SHORT zone: {Instrument.MasterInstrument.FormatPrice(emaLowBuffer)} to {Instrument.MasterInstrument.FormatPrice(emaLow)}");
                    }
                }
                else
                {
                    // Clear buffer zones if disabled or no data
                    emaNoLongZoneStart[timeFrame] = 0;
                    emaNoLongZoneEnd[timeFrame] = 0;
                    emaNoShortZoneStart[timeFrame] = 0;
                    emaNoShortZoneEnd[timeFrame] = 0;
                    
                    if (ShowDebugInfo)
                    {
                        Print($"❌ EMA buffer zones not calculated - thisTimeframeBufferEnabled: {thisTimeframeBufferEnabled}, emaHigh: {emaHigh}, emaLow: {emaLow}, emaHighBuffer: {emaHighBuffer}, emaLowBuffer: {emaLowBuffer}");
                    }
                }
                
                if (ShowDebugInfo && levels.Count > 0)
                    Print($"📄 Read {levels.Count} EMA34SR levels from {fileName}");
                
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error reading EMA34SR file: {ex.Message}");
            }
            
            return levels;
        }
        */
        
        // OLD FILE PARSING LOGIC - COMMENTED OUT (replaced with direct calculation)
        /*
        private SRLevel ParseEMA34SRLine(string line, string timeFrame)
        {
            try
            {
                if (ShowDebugInfo)
                    Print($"🔍 [EMA34SR PARSE] Parsing line: {line}");
                
                int colonIndex = line.IndexOf(':');
                if (colonIndex == -1) 
                {
                    if (ShowDebugInfo)
                        Print($"    ❌ No colon found in line");
                    return null;
                }
                
                string levelName = line.Substring(0, colonIndex);
                string data = line.Substring(colonIndex + 1);
                
                if (ShowDebugInfo)
                {
                    Print($"    Level name: {levelName}");
                    Print($"    Data part: {data}");
                }
                
                // Skip buffer lines for S/R purposes - they are for restrictions only
                if (levelName == "EMA_High_Buffer" || levelName == "EMA_Low_Buffer")
                {
                    if (ShowDebugInfo)
                        Print($"    ⏭️ Skipping buffer level '{levelName}' - not used for S/R analysis");
                    return null;
                }
                
                // Only process actual EMA levels for S/R
                if (levelName != "EMA_High" && levelName != "EMA_Low")
                {
                    if (ShowDebugInfo)
                        Print($"    ⏭️ Skipping non-EMA level '{levelName}'");
                    return null;
                }
                
                // Parse price and timestamp: "price|timestamp"
                string[] parts = data.Split('|');
                if (parts.Length < 1)
                {
                    if (ShowDebugInfo)
                        Print($"    ❌ Invalid data format: {data}");
                    return null;
                }
                
                if (double.TryParse(parts[0], out double price))
                {
                    DateTime timestamp = DateTime.Now; // Default timestamp
                    if (parts.Length > 1 && DateTime.TryParse(parts[1], out DateTime parsedTime))
                        timestamp = parsedTime;
                    
                    var srLevel = new SRLevel
                    {
                        Price = price,
                        Source = "EMA34SR",
                        TimeFrame = timeFrame,
                        Type = levelName, // Store actual level name (EMA_High/EMA_Low) - role determined by trade direction
                        IsActive = true,
                        LastUpdate = timestamp
                    };
                    
                    if (ShowDebugInfo)
                        Print($"    ✅ Parsed: {srLevel.GetDisplayName()} @ {Instrument.MasterInstrument.FormatPrice(price)}");
                    
                    return srLevel;
                }
                else
                {
                    if (ShowDebugInfo)
                        Print($"    ❌ Could not parse price: {parts[0]}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error parsing EMA34SR line: {ex.Message}");
                return null;
            }
        }
        */
        
        // OLD FILENAME PARSING LOGIC - COMMENTED OUT (replaced with direct calculation)
        /*
        private string ExtractTimeFrameFromEMAFileName(string fileName)
        {
            try
            {
                // Expected format: INSTRUMENTNAME_TIMEFRAME_EMA34SR.txt
                // Example: NQ_5MIN_EMA34SR.txt
                
                if (!fileName.Contains("_EMA34SR.txt"))
                    return "UNKNOWN";
                
                string nameWithoutExtension = fileName.Replace("_EMA34SR.txt", "");
                string[] parts = nameWithoutExtension.Split('_');
                
                if (parts.Length >= 2)
                    return parts[parts.Length - 1]; // Last part before _EMA34SR
                
                return "UNKNOWN";
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error extracting timeframe from filename {fileName}: {ex.Message}");
                return "UNKNOWN";
            }
        }
        */
        
        private bool IsLevelRelevant(double levelPrice, double currentPrice)
        {
            double distance = Math.Abs(levelPrice - currentPrice);
            return distance <= MaxSRDistancePoints;
        }
        
        private bool ValidateTradeWithSRFiltering(bool isLong, double entryPrice, double stopLoss, double target)
        {
            try
            {
                if (!IsEMA34SREnabled())
                {
                    if (ShowDebugInfo)
                        Print($"⏭️ [SR FILTER] EMA34SR disabled - trade approved by default");
                    return true; // No S/R filtering, approve by default
                }
                
                // Collect EMA34SR levels
                CollectEMA34SRLevels(entryPrice);
                
                if (allSRLevels.Count == 0)
                {
                    if (ShowDebugInfo)
                        Print($"📊 [SR FILTER] No S/R levels found - trade approved");
                    return true; // No S/R levels found, approve
                }
                
                if (ShowDebugInfo)
                {
                    Print($"📊 [PATH-CROSSING] {(isLong ? "LONG" : "SHORT")} Trade Path Analysis:");
                    Print($"    Entry: {Instrument.MasterInstrument.FormatPrice(entryPrice)}");
                    Print($"    Target: {Instrument.MasterInstrument.FormatPrice(target)}");
                    Print($"    Stop Loss: {Instrument.MasterInstrument.FormatPrice(stopLoss)}");
                }
                
                // PATH-CROSSING LOGIC: Find levels that block the path from entry to target
                SRLevel blockingLevel = null;
                double effectiveTarget = target; // This will be the actual achievable target
                
                foreach (var level in allSRLevels)
                {
                    // Check if this level blocks the trade path from entry to target
                    bool blocksPath = false;
                    
                    if (isLong)
                    {
                        // For LONG: Level blocks path if it's between entry and target
                        blocksPath = (level.Price > entryPrice && level.Price < target);
                    }
                    else
                    {
                        // For SHORT: Level blocks path if it's between entry and target  
                        blocksPath = (level.Price < entryPrice && level.Price > target);
                    }
                    
                    if (blocksPath)
                    {
                        if (ShowDebugInfo)
                            Print($"    🚫 {level.GetDisplayName()} @ {Instrument.MasterInstrument.FormatPrice(level.Price)} BLOCKS path to target");
                        
                        // Find the closest blocking level (first one hit on path to target)
                        if (blockingLevel == null)
                        {
                            blockingLevel = level;
                            effectiveTarget = level.Price; // Can only reach this level, not full target
                        }
                        else
                        {
                            // Update to closer blocking level
                            bool isCloser = isLong ? 
                                (level.Price < blockingLevel.Price) : // For LONG, lower blocking levels are closer
                                (level.Price > blockingLevel.Price);   // For SHORT, higher blocking levels are closer
                            
                            if (isCloser)
                            {
                                blockingLevel = level;
                                effectiveTarget = level.Price;
                            }
                        }
                    }
                    else if (ShowDebugInfo)
                    {
                        string position = isLong ? 
                            (level.Price > target ? "above target" : "below entry") :
                            (level.Price < target ? "below target" : "above entry");
                        Print($"    ✅ {level.GetDisplayName()} @ {Instrument.MasterInstrument.FormatPrice(level.Price)} - {position} (no conflict)");
                    }
                }
                
                if (blockingLevel == null)
                {
                    if (ShowDebugInfo)
                        Print($"✅ [PATH-CROSSING] No levels block path to target - trade approved");
                    return true; // No blocking levels - clear path to target
                }
                
                // Calculate available profit up to the blocking level
                double riskPoints = Math.Abs(entryPrice - stopLoss);
                double availableReward = Math.Abs(effectiveTarget - entryPrice);
                double availableRR = riskPoints > 0 ? availableReward / riskPoints : 0;
                
                // Check if available profit to blocking level meets minimum requirements
                bool meetsSRMinRR = availableRR >= MinimumRiskReward;
                
                // Check ATR-based distance requirement (entry must not be too close to blocking level)
                double currentATR = atr[0];
                double requiredDistanceFromSR = (DistanceFromSRAsATRPercent / 100.0) * currentATR;
                double actualDistanceFromSR = Math.Abs(blockingLevel.Price - entryPrice);
                bool hasEnoughClearance = actualDistanceFromSR >= requiredDistanceFromSR;
                
                if (ShowDebugInfo)
                {
                    Print($"📊 [PATH-CROSSING] Blocking Level Analysis:");
                    Print($"    Blocking Level: {blockingLevel.GetDisplayName()} @ {Instrument.MasterInstrument.FormatPrice(blockingLevel.Price)}");
                    Print($"    Intended Target: {Instrument.MasterInstrument.FormatPrice(target)}");
                    Print($"    Effective Target: {Instrument.MasterInstrument.FormatPrice(effectiveTarget)} (blocked by S/R)");
                    Print($"    Available R/R: {availableRR:F2} (min required: {MinimumRiskReward:F2})");
                    Print($"    Distance from blocking level: {Instrument.MasterInstrument.FormatPrice(actualDistanceFromSR)}");
                    Print($"    Required clearance: {Instrument.MasterInstrument.FormatPrice(requiredDistanceFromSR)} ({DistanceFromSRAsATRPercent:F1}% of ATR)");
                    Print($"    Meets min R/R: {(meetsSRMinRR ? "✅" : "❌")}");
                    Print($"    Has clearance: {(hasEnoughClearance ? "✅" : "❌")}");
                }
                
                bool approved = meetsSRMinRR && hasEnoughClearance;
                
                if (ShowDebugInfo)
                {
                    Print($"🎯 [PATH-CROSSING] Trade Status: {(approved ? "✅ APPROVED" : "❌ REJECTED")}");
                    if (!approved)
                    {
                        string reason = !meetsSRMinRR ? 
                            $"Insufficient profit to blocking level (R/R {availableRR:F2} < {MinimumRiskReward:F2})" :
                            $"Too close to blocking level ({Instrument.MasterInstrument.FormatPrice(actualDistanceFromSR)} < {Instrument.MasterInstrument.FormatPrice(requiredDistanceFromSR)})";
                        Print($"    Rejection reason: {reason}");
                    }
                }
                
                return approved;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ [PATH-CROSSING] Error in S/R validation: {ex.Message}");
                return true; // On error, approve by default to prevent blocking valid trades
            }
        }
        
        // EMA Buffer Zone Validation
        private bool IsEntryBlockedByEMABuffers(double entryPrice, bool isLong, out string blockingReason)
        {
            blockingReason = "";
            
            try
            {
                if (!IsEMABuffersEnabled())
                    return false;
                    
                if (ShowDebugInfo)
                {
                    Print($"🔍 EMA BUFFER CHECK - Entry {entryPrice:F5} ({(isLong ? "LONG" : "SHORT")}):");
                }
                
                // Check all timeframes for buffer zone conflicts
                foreach (var timeFrame in emaNoLongZoneStart.Keys)
                {
                    if (isLong)
                    {
                        // Check if LONG entry is in no-long zone (between top EMA and top EMA + buffer)
                        double zoneStart = emaNoLongZoneStart[timeFrame];
                        double zoneEnd = emaNoLongZoneEnd[timeFrame];
                        
                        if (zoneStart > 0 && zoneEnd > 0)
                        {
                            bool inNoLongZone = entryPrice >= zoneStart && entryPrice <= zoneEnd;
                            
                            if (ShowDebugInfo)
                                Print($"    {timeFrame}: No-LONG zone {zoneStart:F5} to {zoneEnd:F5}, Entry in zone: {inNoLongZone}");
                            
                            if (inNoLongZone)
                            {
                                blockingReason = $"Entry {Instrument.MasterInstrument.FormatPrice(entryPrice)} in {timeFrame} EMA no-long buffer zone ({Instrument.MasterInstrument.FormatPrice(zoneStart)} to {Instrument.MasterInstrument.FormatPrice(zoneEnd)})";
                                
                                if (ShowDebugInfo)
                                    Print($"🚫 LONG BLOCKED by EMA buffer: {blockingReason}");
                                
                                return true;
                            }
                        }
                    }
                    else
                    {
                        // Check if SHORT entry is in no-short zone (between bottom EMA - buffer and bottom EMA)
                        double zoneStart = emaNoShortZoneStart[timeFrame];
                        double zoneEnd = emaNoShortZoneEnd[timeFrame];
                        
                        if (zoneStart > 0 && zoneEnd > 0)
                        {
                            bool inNoShortZone = entryPrice <= zoneStart && entryPrice >= zoneEnd;
                            
                            if (ShowDebugInfo)
                                Print($"    {timeFrame}: No-SHORT zone {zoneEnd:F5} to {zoneStart:F5}, Entry in zone: {inNoShortZone}");
                            
                            if (inNoShortZone)
                            {
                                blockingReason = $"Entry {Instrument.MasterInstrument.FormatPrice(entryPrice)} in {timeFrame} EMA no-short buffer zone ({Instrument.MasterInstrument.FormatPrice(zoneEnd)} to {Instrument.MasterInstrument.FormatPrice(zoneStart)})";
                                
                                if (ShowDebugInfo)
                                    Print($"🚫 SHORT BLOCKED by EMA buffer: {blockingReason}");
                                
                                return true;
                            }
                        }
                    }
                }
                
                if (ShowDebugInfo)
                    Print($"✅ EMA buffer check passed - no buffer zone conflicts");
                
                return false;
            }
            catch (Exception ex)
            {
                if (ShowDebugInfo)
                    Print($"❌ Error checking EMA buffer zones: {ex.Message}");
                
                blockingReason = "Error checking EMA buffers";
                return false; // Don't block on error
            }
        }
    }
}

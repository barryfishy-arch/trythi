#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
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

                    // Reset for next setup
                    hasEMAHighTouched_M30 = false;
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

                    // Reset for next setup
                    hasEMALowTouched_M30 = false;
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

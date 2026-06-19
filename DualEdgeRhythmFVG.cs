// ===========================================================================
// Dual Edge Rhythm+FVG -- Tue/Wed/Thu session variant  [v2 - MTF + Re-Entry]
//
// Strategy: Trend-pullback entries into Fair Value Gaps during a session
// window. Filters stacked across THREE timeframes:
//   - HTF: directional bias gate.
//   - MTF/chart: structure, EMA pullback, or FVG fill.
//   - LTF: lower-timeframe confirmation.
//
// Instrument:    XAGUSD / Silver
// Compatibility: cAlgo / cTrader
//
// FIXES INCLUDED:
//   1) Uses completed bars in OnBar logic instead of forming bar Last(0).
//   2) Session helper supports overnight sessions, e.g. 22 -> 2.
//   3) HTF/LTF filters wait for EMA warm-up instead of using too little data.
//   4) _tradesFired increments only after successful order execution.
//   5) Expired/violated FVGs are explicitly invalidated.
//   6) Optional warning if bot is attached to a non-XAG symbol.
//   7) Hard cutoff hour (default 05:00 UTC): flattens ALL bot-label positions
//      and blocks new entries during that hour, regardless of other settings.
//   8) Risk/session exits and hard cutoff are checked on ticks as well as bars.
//   9) Position close results are checked before journalling them as closed.
//  10) Optional hourly information journal for operational diagnostics.
//  11) Optional fixed-fractional sizing based on account equity and SL distance.
// ===========================================================================

using System;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC)]
    public class DualEdgeRhythmFVG : Robot
    {
        private const int ClosedBarOffset = 1;

        // --- SESSION FILTER ---
        [Parameter("Session Start Hour (UTC)", Group = "Session Filter", DefaultValue = 12, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Session End Hour (UTC)", Group = "Session Filter", DefaultValue = 13, MinValue = 0, MaxValue = 23)]
        public int SessionEndHour { get; set; }

        [Parameter("Only Enter During Session", Group = "Session Filter", DefaultValue = true)]
        public bool UseSessionFilter { get; set; }

        [Parameter("Enable Hard Cutoff (Close All)", Group = "Session Filter", DefaultValue = true)]
        public bool UseHardCutoff { get; set; }

        [Parameter("Hard Cutoff Hour (UTC)", Group = "Session Filter", DefaultValue = 5, MinValue = 0, MaxValue = 23)]
        public int HardCutoffHour { get; set; }

        [Parameter("Close Open Positions at Session End", Group = "Session Filter", DefaultValue = true)]
        public bool CloseAtSessionEnd { get; set; }

        // --- DAY-OF-WEEK FILTER ---
        [Parameter("Enable Day Filter", Group = "Day Filter", DefaultValue = true)]
        public bool UseDayFilter { get; set; }

        [Parameter("Trade Monday", Group = "Day Filter", DefaultValue = false)]
        public bool TradeMon { get; set; }

        [Parameter("Trade Tuesday", Group = "Day Filter", DefaultValue = true)]
        public bool TradeTue { get; set; }

        [Parameter("Trade Wednesday", Group = "Day Filter", DefaultValue = true)]
        public bool TradeWed { get; set; }

        [Parameter("Trade Thursday", Group = "Day Filter", DefaultValue = true)]
        public bool TradeThu { get; set; }

        [Parameter("Trade Friday", Group = "Day Filter", DefaultValue = false)]
        public bool TradeFri { get; set; }

        [Parameter("Block First 5 Days of Month", Group = "Day Filter", DefaultValue = true)]
        public bool BlockEarlyMonth { get; set; }

        // --- TREND DETECTION ---
        [Parameter("Trend EMA Length", Group = "Trend Detection", DefaultValue = 50, MinValue = 10, MaxValue = 200)]
        public int TrendLength { get; set; }

        [Parameter("Swing High/Low Lookback", Group = "Trend Detection", DefaultValue = 10, MinValue = 3, MaxValue = 30)]
        public int StructureLen { get; set; }

        // --- MULTI-TIMEFRAME ---
        [Parameter("Use HTF Bias Filter", Group = "Multi-Timeframe", DefaultValue = true)]
        public bool UseHtfBias { get; set; }

        [Parameter("HTF Timeframe", Group = "Multi-Timeframe", DefaultValue = "Hour4")]
        public TimeFrame HtfTimeFrame { get; set; }

        [Parameter("HTF EMA Length", Group = "Multi-Timeframe", DefaultValue = 50, MinValue = 10, MaxValue = 200)]
        public int HtfEmaLength { get; set; }

        [Parameter("HTF Require Structure Agree", Group = "Multi-Timeframe", DefaultValue = false)]
        public bool HtfRequireStructure { get; set; }

        [Parameter("Use LTF Confirmation", Group = "Multi-Timeframe", DefaultValue = true)]
        public bool UseLtfConfirm { get; set; }

        [Parameter("LTF Timeframe", Group = "Multi-Timeframe", DefaultValue = "Minute5")]
        public TimeFrame LtfTimeFrame { get; set; }

        [Parameter("LTF EMA Length", Group = "Multi-Timeframe", DefaultValue = 21, MinValue = 5, MaxValue = 100)]
        public int LtfEmaLength { get; set; }

        [Parameter("LTF Also Require Candle Colour", Group = "Multi-Timeframe", DefaultValue = false)]
        public bool LtfRequireCandle { get; set; }

        [Parameter("Diagnostic Logging", Group = "Multi-Timeframe", DefaultValue = true)]
        public bool DiagnosticMode { get; set; }

        // --- HOURLY JOURNAL ---
        [Parameter("Enable Hourly Journal", Group = "Hourly Journal", DefaultValue = true)]
        public bool UseHourlyJournal { get; set; }

        [Parameter("Journal MTF Snapshot", Group = "Hourly Journal", DefaultValue = true)]
        public bool JournalMtfSnapshot { get; set; }

        // --- FVG SETTINGS ---
        [Parameter("Minimum FVG Size", Group = "FVG Settings", DefaultValue = 0.01, Step = 0.005)]
        public double FvgMinSize { get; set; }

        [Parameter("Max FVG Age (bars)", Group = "FVG Settings", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int FvgMaxAge { get; set; }

        // --- RISK MANAGEMENT ---
        [Parameter("Lot Size", Group = "Risk Management", DefaultValue = 0.06, MinValue = 0.01, Step = 0.01)]
        public double Lots { get; set; }

        [Parameter("Use Fixed Fractional Sizing", Group = "Risk Management", DefaultValue = false)]
        public bool UseFixedFractionalSizing { get; set; }

        [Parameter("Risk Per Trade %", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10.0, Step = 0.1)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Min FF Lot Size", Group = "Risk Management", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01)]
        public double MinFixedFractionalLots { get; set; }

        [Parameter("Max FF Lot Size", Group = "Risk Management", DefaultValue = 0.50, MinValue = 0.01, Step = 0.01)]
        public double MaxFixedFractionalLots { get; set; }

        [Parameter("Risk:Reward Ratio", Group = "Risk Management", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 5.0, Step = 0.5)]
        public double RiskReward { get; set; }

        [Parameter("ATR Length (SL basis)", Group = "Risk Management", DefaultValue = 14, MinValue = 5, MaxValue = 50)]
        public int AtrLength { get; set; }

        [Parameter("ATR Multiplier for SL", Group = "Risk Management", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 3.0, Step = 0.1)]
        public double AtrMultiplier { get; set; }

        [Parameter("Max Daily Loss %", Group = "Risk Management", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 10.0, Step = 0.5)]
        public double MaxDailyLoss { get; set; }

        [Parameter("Max Trades Per Day", Group = "Risk Management", DefaultValue = 2, MinValue = 1, MaxValue = 5)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Order Label", Group = "Risk Management", DefaultValue = "DualEdge_TueThu")]
        public string Label { get; set; }

        // --- LOSS MANAGEMENT ---
        [Parameter("Enable Max Hold Time Exit", Group = "Loss Management", DefaultValue = true)]
        public bool UseMaxHoldTime { get; set; }

        [Parameter("Max Hold Hours", Group = "Loss Management", DefaultValue = 12, MinValue = 0.5, MaxValue = 48, Step = 0.5)]
        public double MaxHoldHours { get; set; }

        [Parameter("Only Time-Close Losing Trades", Group = "Loss Management", DefaultValue = true)]
        public bool OnlyCloseLosers { get; set; }

        [Parameter("Soft Loss Cut money (0=off)", Group = "Loss Management", DefaultValue = 0.0, MinValue = 0.0, Step = 5.0)]
        public double SoftLossCutMoney { get; set; }

        [Parameter("Soft Cut After Hours", Group = "Loss Management", DefaultValue = 4.0, MinValue = 0.5, MaxValue = 24, Step = 0.5)]
        public double SoftCutAfterHours { get; set; }

        // --- RE-ENTRY CONTROL ---
        [Parameter("Block Same-Direction Re-Entry", Group = "Re-Entry Control", DefaultValue = true)]
        public bool BlockSameDirection { get; set; }

        [Parameter("Same-Direction Cooldown (bars)", Group = "Re-Entry Control", DefaultValue = 3, MinValue = 1, MaxValue = 50)]
        public int SameDirCooldownBars { get; set; }

        [Parameter("Only Block After a Loss", Group = "Re-Entry Control", DefaultValue = true)]
        public bool OnlyBlockAfterLoss { get; set; }

        // --- STATE ---
        private ExponentialMovingAverage _ema;
        private AverageTrueRange _atr;

        private Bars _htfBars;
        private Bars _ltfBars;
        private ExponentialMovingAverage _htfEma;
        private ExponentialMovingAverage _ltfEma;

        private double _lastSwingHigh1 = double.NaN;
        private double _lastSwingHigh2 = double.NaN;
        private double _lastSwingLow1 = double.NaN;
        private double _lastSwingLow2 = double.NaN;

        private double _bullFvgTop = double.NaN;
        private double _bullFvgBottom = double.NaN;
        private int _bullFvgBar = -1;

        private double _bearFvgTop = double.NaN;
        private double _bearFvgBottom = double.NaN;
        private int _bearFvgBar = -1;

        private int _tradesToday = 0;
        private double _dailyStartEquity = 0;
        private bool _dailyLossHit = false;
        private DateTime _currentDay = DateTime.MinValue;

        private TradeType? _lastClosedDirection = null;
        private int _lastClosedBarIndex = -1;
        private bool _lastCloseWasLoss = false;

        private int _signalsSeen = 0;
        private int _htfBlocks = 0;
        private int _ltfBlocks = 0;
        private int _reentryBlocks = 0;
        private int _tradesFired = 0;

        private DateTime _lastJournalHour = DateTime.MinValue;

        private int CurrentClosedBarIndex
        {
            get { return Bars.Count - 2; }
        }

        protected override void OnStart()
        {
            _ema = Indicators.ExponentialMovingAverage(Bars.ClosePrices, TrendLength);
            _atr = Indicators.AverageTrueRange(AtrLength, MovingAverageType.Simple);

            _htfBars = MarketData.GetBars(HtfTimeFrame);
            _ltfBars = MarketData.GetBars(LtfTimeFrame);
            _htfEma = Indicators.ExponentialMovingAverage(_htfBars.ClosePrices, HtfEmaLength);
            _ltfEma = Indicators.ExponentialMovingAverage(_ltfBars.ClosePrices, LtfEmaLength);

            _dailyStartEquity = Account.Equity;
            _currentDay = Server.Time.Date;

            Positions.Closed += OnPositionClosed;

            if (DiagnosticMode && SymbolName.IndexOf("XAG", StringComparison.OrdinalIgnoreCase) < 0)
                Print("WARNING: This bot was designed for XAGUSD/Silver but is attached to {0}.", SymbolName);

            Print("Dual Edge v2 started. Session {0}:00-{1}:00 UTC | Lots {2} | RR {3} | MaxDailyLoss {4}% | MaxHold {5}h losersOnly={6}",
                  SessionStartHour, SessionEndHour, Lots, RiskReward, MaxDailyLoss, MaxHoldHours, OnlyCloseLosers);

            Print("MTF: HTF={0} EMA{1} structAgree={2} | LTF={3} EMA{4} | ReEntryBlock={5} cd={6}bars afterLossOnly={7}",
                  HtfTimeFrame, HtfEmaLength, HtfRequireStructure, LtfTimeFrame, LtfEmaLength,
                  BlockSameDirection, SameDirCooldownBars, OnlyBlockAfterLoss);

            Print("Sizing: mode={0} fixedLots={1} risk={2:F2}% minFF={3} maxFF={4}",
                  UseFixedFractionalSizing ? "fixed-fractional" : "fixed-lot",
                  Lots, RiskPerTradePercent, MinFixedFractionalLots, MaxFixedFractionalLots);

            if (UseHardCutoff)
                Print("HARD CUTOFF enabled: all bot-label positions flattened at {0:00}:00 UTC and no entries during that hour.", HardCutoffHour);

            WriteHourlyJournal("STARTUP", false);
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosed;

            Print("===== DUAL EDGE DIAGNOSTIC SUMMARY =====");
            Print("Signals seen after session/day/setup filters: {0}", _signalsSeen);
            Print("  Blocked by HTF bias gate:         {0}", _htfBlocks);
            Print("  Blocked by LTF confirmation gate: {0}", _ltfBlocks);
            Print("  Blocked by re-entry cooldown:     {0}", _reentryBlocks);
            Print("  Trades successfully fired:        {0}", _tradesFired);

            if (_signalsSeen == 0)
                Print("ZERO setups reached the MTF gates. Check session/day/month filters, structure, pullback, and FVG rules.");
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (p.Label != Label || p.SymbolName != SymbolName)
                return;

            _lastClosedDirection = p.TradeType;
            _lastClosedBarIndex = Bars.Count - 1;
            _lastCloseWasLoss = p.NetProfit < 0;
        }

        protected override void OnTick()
        {
            ManageOpenRisk();
            WriteHourlyJournalIfDue();
        }

        protected override void OnBar()
        {
            ManageOpenRisk();
            WriteHourlyJournalIfDue();

            if (HasOpenPosition())
                return;

            if (!ChartDataReady())
                return;

            UpdateSwings();
            UpdateFvgs();

            if (!AllFiltersPass())
                return;

            EvaluateEntries();
        }

        private void ManageOpenRisk()
        {
            ResetDailyIfNewDay();
            UpdateDailyLossStatus();
            HandleHardCutoff();
            HandleSessionEnd();
            HandleMaxHoldTime();
        }

        private bool ChartDataReady()
        {
            int requiredClosedBars = Math.Max(Math.Max(TrendLength, AtrLength), 2 * StructureLen + 1);

            return Bars.Count >= requiredClosedBars + 1 &&
                   _ema.Result.Count >= requiredClosedBars + 1 &&
                   _atr.Result.Count >= AtrLength + 1;
        }

        private void ResetDailyIfNewDay()
        {
            DateTime today = Server.Time.Date;

            if (today != _currentDay)
            {
                _currentDay = today;
                _tradesToday = 0;
                _dailyStartEquity = Account.Equity;
                _dailyLossHit = false;
            }
        }

        private void UpdateDailyLossStatus()
        {
            if (_dailyStartEquity <= 0)
                return;

            double dailyLossPct = GetDailyLossPercent();

            if (dailyLossPct >= MaxDailyLoss)
            {
                if (!_dailyLossHit)
                    Print("DAILY LOSS LIMIT HIT: {0:F2}% - no new trades today.", dailyLossPct);

                _dailyLossHit = true;
            }
        }

        private double GetDailyLossPercent()
        {
            if (_dailyStartEquity <= 0)
                return 0;

            return (_dailyStartEquity - Account.Equity) / _dailyStartEquity * 100.0;
        }

        private bool IsInSession()
        {
            int currentHour = Server.Time.Hour;

            if (SessionStartHour == SessionEndHour)
                return true;

            if (SessionStartHour < SessionEndHour)
                return currentHour >= SessionStartHour && currentHour < SessionEndHour;

            return currentHour >= SessionStartHour || currentHour < SessionEndHour;
        }

        private bool IsInHardCutoffHour()
        {
            return UseHardCutoff && Server.Time.Hour == HardCutoffHour;
        }

        private DateTime GetMostRecentHardCutoffTime()
        {
            DateTime cutoff = Server.Time.Date.AddHours(HardCutoffHour);

            if (Server.Time < cutoff)
                cutoff = cutoff.AddDays(-1);

            return cutoff;
        }

        private void HandleHardCutoff()
        {
            if (!UseHardCutoff)
                return;

            DateTime cutoff = GetMostRecentHardCutoffTime();
            bool inCutoffHour = IsInHardCutoffHour();

            foreach (var position in Positions.FindAll(Label))
            {
                if (inCutoffHour || position.EntryTime < cutoff)
                    CloseManagedPosition(position, string.Format("HARD CUTOFF {0:00}:00 UTC", HardCutoffHour));
            }
        }

        private void HandleSessionEnd()
        {
            if (!CloseAtSessionEnd || !UseSessionFilter)
                return;

            if (IsInSession())
                return;

            foreach (var position in Positions.FindAll(Label, SymbolName))
                CloseManagedPosition(position, "Position closed outside session");
        }

        private void HandleMaxHoldTime()
        {
            if (!UseMaxHoldTime && SoftLossCutMoney <= 0)
                return;

            foreach (var position in Positions.FindAll(Label, SymbolName))
            {
                double hoursOpen = (Server.Time - position.EntryTime).TotalHours;

                if (UseMaxHoldTime && hoursOpen >= MaxHoldHours)
                {
                    if (OnlyCloseLosers && position.NetProfit >= 0)
                        continue;

                    CloseManagedPosition(position, string.Format("MAX-HOLD exit {0:F1}h", hoursOpen));
                    continue;
                }

                if (SoftLossCutMoney > 0 &&
                    hoursOpen >= SoftCutAfterHours &&
                    position.NetProfit <= -SoftLossCutMoney)
                {
                    CloseManagedPosition(position, string.Format("SOFT-LOSS cut after {0:F1}h", hoursOpen));
                }
            }
        }

        private bool CloseManagedPosition(Position position, string reason)
        {
            var result = ClosePosition(position);

            if (!result.IsSuccessful)
            {
                Print("{0} - close failed. ID {1} Symbol {2} Error {3}",
                      reason, position.Id, position.SymbolName, result.Error);
                return false;
            }

            Print("{0} - position closed. ID {1} Symbol {2} Net {3:F2}",
                  reason, position.Id, position.SymbolName, position.NetProfit);
            return true;
        }

        private bool HasOpenPosition()
        {
            return Positions.FindAll(Label, SymbolName).Length > 0;
        }

        private bool IsSameDirectionBlocked(TradeType direction)
        {
            if (!BlockSameDirection)
                return false;

            if (_lastClosedDirection == null)
                return false;

            if (_lastClosedDirection.Value != direction)
                return false;

            if (OnlyBlockAfterLoss && !_lastCloseWasLoss)
                return false;

            int barsSinceClose = (Bars.Count - 1) - _lastClosedBarIndex;
            return barsSinceClose < SameDirCooldownBars;
        }

        private bool HtfDataReady()
        {
            int requiredBars = HtfEmaLength + 4;

            return _htfBars != null &&
                   _htfBars.Count >= requiredBars &&
                   _htfEma.Result.Count >= requiredBars;
        }

        private bool LtfDataReady()
        {
            int requiredBars = LtfEmaLength + 2;

            return _ltfBars != null &&
                   _ltfBars.Count >= requiredBars &&
                   _ltfEma.Result.Count >= requiredBars;
        }

        private bool HtfAllowsLong()
        {
            if (!UseHtfBias)
                return true;

            if (!HtfDataReady())
            {
                if (DiagnosticMode)
                    Print("LONG blocked - HTF data/EMA not ready yet.");

                return false;
            }

            double htfClose = _htfBars.ClosePrices.Last(ClosedBarOffset);
            double htfEma = _htfEma.Result.Last(ClosedBarOffset);
            bool emaBull = htfClose > htfEma;

            if (!HtfRequireStructure)
                return emaBull;

            bool emaRising = _htfEma.Result.Last(1) > _htfEma.Result.Last(3);
            return emaBull && emaRising;
        }

        private bool HtfAllowsShort()
        {
            if (!UseHtfBias)
                return true;

            if (!HtfDataReady())
            {
                if (DiagnosticMode)
                    Print("SHORT blocked - HTF data/EMA not ready yet.");

                return false;
            }

            double htfClose = _htfBars.ClosePrices.Last(ClosedBarOffset);
            double htfEma = _htfEma.Result.Last(ClosedBarOffset);
            bool emaBear = htfClose < htfEma;

            if (!HtfRequireStructure)
                return emaBear;

            bool emaFalling = _htfEma.Result.Last(1) < _htfEma.Result.Last(3);
            return emaBear && emaFalling;
        }

        private bool LtfConfirmsLong()
        {
            if (!UseLtfConfirm)
                return true;

            if (!LtfDataReady())
            {
                if (DiagnosticMode)
                    Print("LONG blocked - LTF data/EMA not ready yet.");

                return false;
            }

            bool aboveEma = _ltfBars.ClosePrices.Last(ClosedBarOffset) > _ltfEma.Result.Last(ClosedBarOffset);

            if (!LtfRequireCandle)
                return aboveEma;

            bool bullBar = _ltfBars.ClosePrices.Last(ClosedBarOffset) > _ltfBars.OpenPrices.Last(ClosedBarOffset);
            return aboveEma && bullBar;
        }

        private bool LtfConfirmsShort()
        {
            if (!UseLtfConfirm)
                return true;

            if (!LtfDataReady())
            {
                if (DiagnosticMode)
                    Print("SHORT blocked - LTF data/EMA not ready yet.");

                return false;
            }

            bool belowEma = _ltfBars.ClosePrices.Last(ClosedBarOffset) < _ltfEma.Result.Last(ClosedBarOffset);

            if (!LtfRequireCandle)
                return belowEma;

            bool bearBar = _ltfBars.ClosePrices.Last(ClosedBarOffset) < _ltfBars.OpenPrices.Last(ClosedBarOffset);
            return belowEma && bearBar;
        }

        private void UpdateSwings()
        {
            double pivotHigh = DetectPivotHigh(StructureLen);

            if (!double.IsNaN(pivotHigh))
            {
                _lastSwingHigh2 = _lastSwingHigh1;
                _lastSwingHigh1 = pivotHigh;
            }

            double pivotLow = DetectPivotLow(StructureLen);

            if (!double.IsNaN(pivotLow))
            {
                _lastSwingLow2 = _lastSwingLow1;
                _lastSwingLow1 = pivotLow;
            }
        }

        private double DetectPivotHigh(int lookback)
        {
            if (Bars.Count < 2 * lookback + 2)
                return double.NaN;

            int centerOffset = lookback + ClosedBarOffset;
            double pivotValue = Bars.HighPrices.Last(centerOffset);

            for (int i = 1; i <= lookback; i++)
            {
                if (Bars.HighPrices.Last(centerOffset - i) >= pivotValue)
                    return double.NaN;

                if (Bars.HighPrices.Last(centerOffset + i) >= pivotValue)
                    return double.NaN;
            }

            return pivotValue;
        }

        private double DetectPivotLow(int lookback)
        {
            if (Bars.Count < 2 * lookback + 2)
                return double.NaN;

            int centerOffset = lookback + ClosedBarOffset;
            double pivotValue = Bars.LowPrices.Last(centerOffset);

            for (int i = 1; i <= lookback; i++)
            {
                if (Bars.LowPrices.Last(centerOffset - i) <= pivotValue)
                    return double.NaN;

                if (Bars.LowPrices.Last(centerOffset + i) <= pivotValue)
                    return double.NaN;
            }

            return pivotValue;
        }

        private void UpdateFvgs()
        {
            if (Bars.Count < 4)
                return;

            int completedBarIndex = CurrentClosedBarIndex;
            double completedLow = Bars.LowPrices.Last(ClosedBarOffset);
            double completedHigh = Bars.HighPrices.Last(ClosedBarOffset);

            if (_bullFvgBar >= 0)
            {
                int age = completedBarIndex - _bullFvgBar;

                if (age > FvgMaxAge || completedLow <= _bullFvgBottom)
                    ClearBullFvg();
            }

            if (_bearFvgBar >= 0)
            {
                int age = completedBarIndex - _bearFvgBar;

                if (age > FvgMaxAge || completedHigh >= _bearFvgTop)
                    ClearBearFvg();
            }

            double highTwoBarsBack = Bars.HighPrices.Last(3);
            double lowCurrentClosed = Bars.LowPrices.Last(ClosedBarOffset);

            if (highTwoBarsBack < lowCurrentClosed && (lowCurrentClosed - highTwoBarsBack) >= FvgMinSize)
            {
                _bullFvgTop = lowCurrentClosed;
                _bullFvgBottom = highTwoBarsBack;
                _bullFvgBar = completedBarIndex;
            }

            double lowTwoBarsBack = Bars.LowPrices.Last(3);
            double highCurrentClosed = Bars.HighPrices.Last(ClosedBarOffset);

            if (lowTwoBarsBack > highCurrentClosed && (lowTwoBarsBack - highCurrentClosed) >= FvgMinSize)
            {
                _bearFvgTop = lowTwoBarsBack;
                _bearFvgBottom = highCurrentClosed;
                _bearFvgBar = completedBarIndex;
            }
        }

        private void ClearBullFvg()
        {
            _bullFvgTop = double.NaN;
            _bullFvgBottom = double.NaN;
            _bullFvgBar = -1;
        }

        private void ClearBearFvg()
        {
            _bearFvgTop = double.NaN;
            _bearFvgBottom = double.NaN;
            _bearFvgBar = -1;
        }

        private bool AllFiltersPass()
        {
            if (_tradesToday >= MaxTradesPerDay)
                return false;

            if (_dailyLossHit)
                return false;

            if (IsInHardCutoffHour())
                return false;

            if (UseSessionFilter && !IsInSession())
                return false;

            if (UseDayFilter && !IsTradeDayAllowed())
                return false;

            if (IsEarlyMonthBlocked())
                return false;

            return true;
        }

        private bool IsTradeDayAllowed()
        {
            DayOfWeek today = Server.Time.DayOfWeek;

            return (today == DayOfWeek.Monday && TradeMon) ||
                   (today == DayOfWeek.Tuesday && TradeTue) ||
                   (today == DayOfWeek.Wednesday && TradeWed) ||
                   (today == DayOfWeek.Thursday && TradeThu) ||
                   (today == DayOfWeek.Friday && TradeFri);
        }

        private bool IsEarlyMonthBlocked()
        {
            return BlockEarlyMonth && Server.Time.Day <= 5;
        }

        private void EvaluateEntries()
        {
            double close = Bars.ClosePrices.Last(ClosedBarOffset);
            double high = Bars.HighPrices.Last(ClosedBarOffset);
            double low = Bars.LowPrices.Last(ClosedBarOffset);
            double ema = _ema.Result.Last(ClosedBarOffset);

            bool structureReady = !double.IsNaN(_lastSwingHigh1) && !double.IsNaN(_lastSwingHigh2) &&
                                  !double.IsNaN(_lastSwingLow1) && !double.IsNaN(_lastSwingLow2);

            if (!structureReady)
                return;

            bool bullishStructure = _lastSwingHigh1 > _lastSwingHigh2 &&
                                    _lastSwingLow1 > _lastSwingLow2 &&
                                    close > ema;

            bool bearishStructure = _lastSwingHigh1 < _lastSwingHigh2 &&
                                    _lastSwingLow1 < _lastSwingLow2 &&
                                    close < ema;

            if (!bullishStructure && !bearishStructure)
                return;

            bool pullbackToBuy = bullishStructure && low <= ema * 1.002 && close > ema;
            bool pullbackToSell = bearishStructure && high >= ema * 0.998 && close < ema;

            int currentBarIndex = CurrentClosedBarIndex;

            int bullFvgAge = _bullFvgBar >= 0 ? currentBarIndex - _bullFvgBar : -1;
            int bearFvgAge = _bearFvgBar >= 0 ? currentBarIndex - _bearFvgBar : -1;

            bool bullFvgValid = _bullFvgBar >= 0 &&
                                bullFvgAge > 0 &&
                                bullFvgAge <= FvgMaxAge &&
                                low > _bullFvgBottom;

            bool bearFvgValid = _bearFvgBar >= 0 &&
                                bearFvgAge > 0 &&
                                bearFvgAge <= FvgMaxAge &&
                                high < _bearFvgTop;

            bool priceInBullFvg = bullFvgValid &&
                                  low <= _bullFvgTop &&
                                  close >= _bullFvgBottom;

            bool priceInBearFvg = bearFvgValid &&
                                  high >= _bearFvgBottom &&
                                  close <= _bearFvgTop;

            if (bullishStructure && (pullbackToBuy || priceInBullFvg))
            {
                _signalsSeen++;

                if (!HtfAllowsLong())
                {
                    _htfBlocks++;

                    if (DiagnosticMode)
                        Print("LONG skipped - HTF bias not bullish or not ready ({0}).", HtfTimeFrame);

                    return;
                }

                if (!LtfConfirmsLong())
                {
                    _ltfBlocks++;

                    if (DiagnosticMode)
                        Print("LONG skipped - LTF momentum not confirming or not ready ({0}).", LtfTimeFrame);

                    return;
                }

                if (IsSameDirectionBlocked(TradeType.Buy))
                {
                    _reentryBlocks++;

                    if (DiagnosticMode)
                        Print("LONG skipped - same-direction cooldown active ({0} bars).", SameDirCooldownBars);

                    return;
                }

                if (ExecuteEntry(TradeType.Buy) && priceInBullFvg)
                    ClearBullFvg();
            }
            else if (bearishStructure && (pullbackToSell || priceInBearFvg))
            {
                _signalsSeen++;

                if (!HtfAllowsShort())
                {
                    _htfBlocks++;

                    if (DiagnosticMode)
                        Print("SHORT skipped - HTF bias not bearish or not ready ({0}).", HtfTimeFrame);

                    return;
                }

                if (!LtfConfirmsShort())
                {
                    _ltfBlocks++;

                    if (DiagnosticMode)
                        Print("SHORT skipped - LTF momentum not confirming or not ready ({0}).", LtfTimeFrame);

                    return;
                }

                if (IsSameDirectionBlocked(TradeType.Sell))
                {
                    _reentryBlocks++;

                    if (DiagnosticMode)
                        Print("SHORT skipped - same-direction cooldown active ({0} bars).", SameDirCooldownBars);

                    return;
                }

                if (ExecuteEntry(TradeType.Sell) && priceInBearFvg)
                    ClearBearFvg();
            }
        }

        private bool ExecuteEntry(TradeType direction)
        {
            double atrValue = _atr.Result.Last(ClosedBarOffset);

            if (double.IsNaN(atrValue) || atrValue <= 0)
            {
                Print("ATR not ready or invalid - skipping entry.");
                return false;
            }

            double slDistance = atrValue * AtrMultiplier;
            double tpDistance = slDistance * RiskReward;

            double slPips = slDistance / Symbol.PipSize;
            double tpPips = tpDistance / Symbol.PipSize;

            double volume = CalculateEntryVolume(slPips);

            if (volume <= 0)
            {
                Print("Calculated volume is zero - skipping entry. Check sizing parameters and symbol minimum.");
                return false;
            }

            var result = ExecuteMarketOrder(direction, SymbolName, volume, Label, slPips, tpPips);

            if (!result.IsSuccessful)
            {
                Print("Order failed: {0}", result.Error);
                return false;
            }

            _tradesToday++;
            _tradesFired++;

            Print("{0} entry filled. Volume {1} SL {2:F1} pips TP {3:F1} pips Trades {4}/{5}",
                  direction, volume, slPips, tpPips, _tradesToday, MaxTradesPerDay);

            return true;
        }

        private double CalculateEntryVolume(double stopLossPips)
        {
            if (!UseFixedFractionalSizing)
                return NormalizeLotsToVolume(Lots);

            if (stopLossPips <= 0 || Symbol.PipValue <= 0)
            {
                Print("Fixed-fractional sizing failed: invalid SL pips {0:F2} or PipValue {1}.",
                      stopLossPips, Symbol.PipValue);
                return 0;
            }

            double riskMoney = Account.Equity * (RiskPerTradePercent / 100.0);
            double rawVolume = riskMoney / (stopLossPips * Symbol.PipValue);
            double minVolume = NormalizeLotsToVolume(MinFixedFractionalLots);
            double maxVolume = NormalizeLotsToVolume(MaxFixedFractionalLots);

            if (maxVolume > 0 && rawVolume > maxVolume)
                rawVolume = maxVolume;

            double volume = Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.Down);

            if (minVolume > 0 && volume < minVolume)
            {
                Print("Fixed-fractional volume {0} is below minimum configured volume {1}; skipping entry.",
                      volume, minVolume);
                return 0;
            }

            Print("Fixed-fractional sizing: equity={0:F2} risk={1:F2}% riskMoney={2:F2} sl={3:F1}p rawVol={4:F0} finalVol={5:F0}",
                  Account.Equity, RiskPerTradePercent, riskMoney, stopLossPips, rawVolume, volume);

            return volume;
        }

        private double NormalizeLotsToVolume(double lots)
        {
            double volume = Symbol.QuantityToVolumeInUnits(lots);
            return Symbol.NormalizeVolumeInUnits(volume, RoundingMode.Down);
        }

        private void WriteHourlyJournalIfDue()
        {
            if (!UseHourlyJournal)
                return;

            DateTime currentHour = Server.Time.Date.AddHours(Server.Time.Hour);

            if (currentHour == _lastJournalHour)
                return;

            WriteHourlyJournal("HOURLY", false);
        }

        private void WriteHourlyJournal(string reason, bool force)
        {
            if (!UseHourlyJournal && !force)
                return;

            DateTime currentHour = Server.Time.Date.AddHours(Server.Time.Hour);
            _lastJournalHour = currentHour;

            int allBotPositions = Positions.FindAll(Label).Length;
            int symbolPositions = Positions.FindAll(Label, SymbolName).Length;
            string sessionState = IsInHardCutoffHour() ? "hard-cutoff" : (IsInSession() ? "in-session" : "out-of-session");
            string dayState = UseDayFilter ? (IsTradeDayAllowed() ? "allowed" : "blocked") : "filter-off";
            string monthState = IsEarlyMonthBlocked() ? "blocked-early-month" : "allowed";
            string sizingState = UseFixedFractionalSizing
                ? string.Format("fixed-fractional risk={0:F2}% minLot={1} maxLot={2}", RiskPerTradePercent, MinFixedFractionalLots, MaxFixedFractionalLots)
                : string.Format("fixed-lot lots={0}", Lots);
            string chartState = GetChartJournalSnapshot();
            string fvgState = GetFvgJournalSnapshot();
            string mtfState = JournalMtfSnapshot ? " | " + GetMtfJournalSnapshot() : string.Empty;

            Print("{0} JOURNAL {1:yyyy-MM-dd HH}:00 UTC | {2} | day={3} month={4} | equity={5:F2} dailyLoss={6:F2}% hit={7} | sizing={8} | trades={9}/{10} fired={11} | positions label/all={12} symbol={13} | {14} | {15} | signals={16} htfBlk={17} ltfBlk={18} reentryBlk={19}{20}",
                  reason,
                  currentHour,
                  sessionState,
                  dayState,
                  monthState,
                  Account.Equity,
                  GetDailyLossPercent(),
                  _dailyLossHit,
                  sizingState,
                  _tradesToday,
                  MaxTradesPerDay,
                  _tradesFired,
                  allBotPositions,
                  symbolPositions,
                  chartState,
                  fvgState,
                  _signalsSeen,
                  _htfBlocks,
                  _ltfBlocks,
                  _reentryBlocks,
                  mtfState);
        }

        private string GetChartJournalSnapshot()
        {
            if (!ChartDataReady())
                return string.Format("chart not ready bars={0}", Bars.Count);

            double close = Bars.ClosePrices.Last(ClosedBarOffset);
            double ema = _ema.Result.Last(ClosedBarOffset);
            double atr = _atr.Result.Last(ClosedBarOffset);

            string structure = "structure=not-ready";

            if (!double.IsNaN(_lastSwingHigh1) && !double.IsNaN(_lastSwingHigh2) &&
                !double.IsNaN(_lastSwingLow1) && !double.IsNaN(_lastSwingLow2))
            {
                bool higherHighs = _lastSwingHigh1 > _lastSwingHigh2;
                bool higherLows = _lastSwingLow1 > _lastSwingLow2;
                bool lowerHighs = _lastSwingHigh1 < _lastSwingHigh2;
                bool lowerLows = _lastSwingLow1 < _lastSwingLow2;

                if (higherHighs && higherLows)
                    structure = "structure=bullish";
                else if (lowerHighs && lowerLows)
                    structure = "structure=bearish";
                else
                    structure = "structure=mixed";
            }

            return string.Format("chart close={0:F5} ema={1:F5} atr={2:F5} {3}",
                                 close, ema, atr, structure);
        }

        private string GetFvgJournalSnapshot()
        {
            return string.Format("{0}; {1}", FormatFvg("bull", _bullFvgTop, _bullFvgBottom, _bullFvgBar),
                                 FormatFvg("bear", _bearFvgTop, _bearFvgBottom, _bearFvgBar));
        }

        private string FormatFvg(string side, double top, double bottom, int barIndex)
        {
            if (barIndex < 0)
                return side + "FVG=none";

            int age = CurrentClosedBarIndex >= 0 ? CurrentClosedBarIndex - barIndex : 0;
            return string.Format("{0}FVG age={1} top={2:F5} bottom={3:F5}", side, age, top, bottom);
        }

        private string GetMtfJournalSnapshot()
        {
            string htfState = "HTF=off";
            string ltfState = "LTF=off";

            if (UseHtfBias)
            {
                htfState = HtfDataReady()
                    ? string.Format("HTF {0} close={1:F5} ema={2:F5}", HtfTimeFrame, _htfBars.ClosePrices.Last(ClosedBarOffset), _htfEma.Result.Last(ClosedBarOffset))
                    : string.Format("HTF {0} not-ready bars={1}", HtfTimeFrame, _htfBars == null ? 0 : _htfBars.Count);
            }

            if (UseLtfConfirm)
            {
                ltfState = LtfDataReady()
                    ? string.Format("LTF {0} close={1:F5} ema={2:F5}", LtfTimeFrame, _ltfBars.ClosePrices.Last(ClosedBarOffset), _ltfEma.Result.Last(ClosedBarOffset))
                    : string.Format("LTF {0} not-ready bars={1}", LtfTimeFrame, _ltfBars == null ? 0 : _ltfBars.Count);
            }

            return htfState + " | " + ltfState;
        }
    }
}

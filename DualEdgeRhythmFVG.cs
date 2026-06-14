// ===========================================================================
// Dual Edge Rhythm+FVG -- Tue/Wed/Thu session variant  [v3 - Risk Overhaul]
//
// Strategy: Trend-pullback entries into Fair Value Gaps during a session
// window. Filters stacked across THREE timeframes:
//   - HTF: directional bias gate.
//   - MTF/chart: structure, EMA pullback, or FVG fill.
//   - LTF: lower-timeframe confirmation.
//
// Instrument:    XAUUSD / Gold
// Compatibility: cAlgo / cTrader
//
// v2 FIXES:
//   1) Uses completed bars in OnBar logic instead of forming bar Last(0).
//   2) Session helper supports overnight sessions, e.g. 22 -> 2.
//   3) HTF/LTF filters wait for EMA warm-up instead of using too little data.
//   4) _tradesFired increments only after successful order execution.
//   5) Expired/violated FVGs are explicitly invalidated.
//   6) Optional warning if bot is attached to a non-XAG symbol.
//   7) Hard cutoff hour (default 05:00 UTC): flattens ALL bot positions and
//      blocks new entries during that hour, regardless of other settings.
//
// v3 RISK OVERHAUL:
//   8)  Percent-of-equity position sizing (constant $ risk per trade despite
//       the ATR-based variable stop). Fixed lots remain as a fallback mode.
//   9)  All protective exits (hard cutoff, session end, max hold, soft loss,
//       daily loss, kill switch, breakeven/trailing) run on EVERY TICK, not
//       just on bar open.
//   10) Stop loss is verified after each fill; a naked position is closed
//       immediately.
//   11) Daily trade count and daily PnL are rebuilt from History on restart,
//       so a bot restart cannot double the per-day allowances.
//   12) Swings/FVGs keep updating while a position is open (no stale state).
//   13) Daily loss limit is label-scoped (this bot's trades only), not
//       whole-account equity.
//   14) Daily news blackout window (default 12:25-12:40 UTC covers 8:30 ET
//       US releases) + max-spread-vs-SL entry guard.
//   15) Breakeven move at +1R (small offset) and optional ATR trailing stop.
//       Initial risk is stored in the position comment so it survives
//       restarts.
//   16) ATR-relative FVG minimum size and EMA pullback tolerance (legacy
//       absolute/percent thresholds available via toggle).
//   17) Account-level kill switch: max total drawdown from starting equity
//       flattens the bot's positions and halts all trading.
// ===========================================================================

using System;
using System.Globalization;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC)]
    public class DualEdgeRhythmFVG : Robot
    {
        private const int ClosedBarOffset = 1;
        private const string RiskCommentPrefix = "R=";
        private static readonly double[] FibRetracements = { 0.382, 0.5, 0.618, 0.786 };
        private static readonly int[] FibTimeCounts = { 5, 8, 13, 21 };
        private const double FibPriceScoreWeight = 0.5;
        private const double FibTimeScoreWeight = 0.2;
        private const double FibFvgScoreWeight = 0.3;
        private const double FibExtension1272 = 1.272;
        private const double FibExtension1618 = 1.618;

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

        // --- NEWS FILTER ---
        [Parameter("Enable News Blackout", Group = "News Filter", DefaultValue = true)]
        public bool UseNewsBlackout { get; set; }

        [Parameter("Blackout Start Hour (UTC)", Group = "News Filter", DefaultValue = 12, MinValue = 0, MaxValue = 23)]
        public int NewsBlackoutHour { get; set; }

        [Parameter("Blackout Start Minute", Group = "News Filter", DefaultValue = 25, MinValue = 0, MaxValue = 59)]
        public int NewsBlackoutMinute { get; set; }

        [Parameter("Blackout Duration (minutes)", Group = "News Filter", DefaultValue = 15, MinValue = 1, MaxValue = 180)]
        public int NewsBlackoutDuration { get; set; }

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

        [Parameter("Pullback Tolerance (ATR mult)", Group = "Trend Detection", DefaultValue = 0.3, MinValue = 0.05, MaxValue = 2.0, Step = 0.05)]
        public double PullbackAtrTolerance { get; set; }

        // --- FIB SCORE ARM ---
        [Parameter("Enable Fib Swing Score", Group = "Fib Score Arm", DefaultValue = true)]
        public bool UseFibSwingScore { get; set; }

        [Parameter("XAU Short Bias Only", Group = "Fib Score Arm", DefaultValue = true)]
        public bool FibShortBiasOnly { get; set; }

        [Parameter("Score Threshold", Group = "Fib Score Arm", DefaultValue = 0.7, MinValue = 0.1, MaxValue = 1.0, Step = 0.05)]
        public double FibScoreThreshold { get; set; }

        [Parameter("Zone Tolerance (range)", Group = "Fib Score Arm", DefaultValue = 0.05, MinValue = 0.005, MaxValue = 0.5, Step = 0.005)]
        public double FibZoneTolerance { get; set; }

        [Parameter("Swing Stop Buffer (ATR mult)", Group = "Fib Score Arm", DefaultValue = 0.1, MinValue = 0.0, MaxValue = 2.0, Step = 0.05)]
        public double FibSwingStopBufferAtr { get; set; }

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

        // --- FVG SETTINGS ---
        [Parameter("Use ATR-Relative Thresholds", Group = "FVG Settings", DefaultValue = true)]
        public bool UseAtrRelative { get; set; }

        [Parameter("FVG Min Size (ATR mult)", Group = "FVG Settings", DefaultValue = 0.05, MinValue = 0.01, MaxValue = 2.0, Step = 0.01)]
        public double FvgMinSizeAtr { get; set; }

        [Parameter("Minimum FVG Size (legacy, price)", Group = "FVG Settings", DefaultValue = 0.01, Step = 0.005)]
        public double FvgMinSize { get; set; }

        [Parameter("Max FVG Age (bars)", Group = "FVG Settings", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int FvgMaxAge { get; set; }

        // --- RISK MANAGEMENT ---
        [Parameter("Use % Risk Sizing", Group = "Risk Management", DefaultValue = true)]
        public bool UsePercentRisk { get; set; }

        [Parameter("Risk % of Equity per Trade", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0, Step = 0.1)]
        public double RiskPercent { get; set; }

        [Parameter("Max Lots Cap", Group = "Risk Management", DefaultValue = 0.5, MinValue = 0.01, Step = 0.01)]
        public double MaxLots { get; set; }

        [Parameter("Lot Size (if % sizing off)", Group = "Risk Management", DefaultValue = 0.06, MinValue = 0.01, Step = 0.01)]
        public double Lots { get; set; }

        [Parameter("Max Spread (% of SL, 0=off)", Group = "Risk Management", DefaultValue = 10.0, MinValue = 0.0, MaxValue = 50.0, Step = 1.0)]
        public double MaxSpreadPctOfSl { get; set; }

        [Parameter("Risk:Reward Ratio", Group = "Risk Management", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 5.0, Step = 0.5)]
        public double RiskReward { get; set; }

        [Parameter("ATR Length (SL basis)", Group = "Risk Management", DefaultValue = 14, MinValue = 5, MaxValue = 50)]
        public int AtrLength { get; set; }

        [Parameter("ATR Multiplier for SL", Group = "Risk Management", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 3.0, Step = 0.1)]
        public double AtrMultiplier { get; set; }

        [Parameter("Max Daily Loss %", Group = "Risk Management", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 10.0, Step = 0.5)]
        public double MaxDailyLoss { get; set; }

        [Parameter("Max Total Drawdown % (0=off)", Group = "Risk Management", DefaultValue = 10.0, MinValue = 0.0, MaxValue = 50.0, Step = 1.0)]
        public double MaxTotalDrawdown { get; set; }

        [Parameter("Max Trades Per Day", Group = "Risk Management", DefaultValue = 2, MinValue = 1, MaxValue = 5)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Order Label", Group = "Risk Management", DefaultValue = "DualEdge_TueThu")]
        public string Label { get; set; }

        // --- TRADE MANAGEMENT ---
        [Parameter("Enable Breakeven", Group = "Trade Management", DefaultValue = true)]
        public bool UseBreakeven { get; set; }

        [Parameter("Breakeven Trigger (R)", Group = "Trade Management", DefaultValue = 1.0, MinValue = 0.2, MaxValue = 3.0, Step = 0.1)]
        public double BreakevenTriggerR { get; set; }

        [Parameter("Breakeven Offset (pips)", Group = "Trade Management", DefaultValue = 1.0, MinValue = 0.0, Step = 0.5)]
        public double BreakevenOffsetPips { get; set; }

        [Parameter("Enable ATR Trailing Stop", Group = "Trade Management", DefaultValue = false)]
        public bool UseAtrTrail { get; set; }

        [Parameter("Trail Start (R)", Group = "Trade Management", DefaultValue = 1.0, MinValue = 0.2, MaxValue = 5.0, Step = 0.1)]
        public double TrailStartR { get; set; }

        [Parameter("Trail ATR Multiplier", Group = "Trade Management", DefaultValue = 1.0, MinValue = 0.3, MaxValue = 3.0, Step = 0.1)]
        public double TrailAtrMultiplier { get; set; }

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
        private int _lastSwingHighBar1 = -1;
        private int _lastSwingHighBar2 = -1;
        private int _lastSwingLowBar1 = -1;
        private int _lastSwingLowBar2 = -1;

        private double _bullFvgTop = double.NaN;
        private double _bullFvgBottom = double.NaN;
        private int _bullFvgBar = -1;

        private double _bearFvgTop = double.NaN;
        private double _bearFvgBottom = double.NaN;
        private int _bearFvgBar = -1;

        private int _tradesToday = 0;
        private double _dailyStartEquity = 0;
        private double _realizedToday = 0;
        private double _floatingAtDayStart = 0;
        private bool _dailyLossHit = false;
        private DateTime _currentDay = DateTime.MinValue;

        private double _startingEquity = 0;
        private bool _killSwitchHit = false;

        private TradeType? _lastClosedDirection = null;
        private int _lastClosedBarIndex = -1;
        private bool _lastCloseWasLoss = false;

        private int _signalsSeen = 0;
        private int _htfBlocks = 0;
        private int _ltfBlocks = 0;
        private int _reentryBlocks = 0;
        private int _tradesFired = 0;

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

            _startingEquity = Account.Equity;
            _currentDay = Server.Time.Date;

            RestoreDailyStateFromHistory();

            Positions.Closed += OnPositionClosed;

            if (DiagnosticMode && SymbolName.IndexOf("XAU", StringComparison.OrdinalIgnoreCase) < 0)
                Print("WARNING: This bot was designed for XAUUSD/Gold but is attached to {0}.", SymbolName);

            Print("Dual Edge v3 started. Session {0}:00-{1}:00 UTC | RR {2} | MaxDailyLoss {3}% | MaxHold {4}h losersOnly={5}",
                  SessionStartHour, SessionEndHour, RiskReward, MaxDailyLoss, MaxHoldHours, OnlyCloseLosers);

            Print("MTF: HTF={0} EMA{1} structAgree={2} | LTF={3} EMA{4} | ReEntryBlock={5} cd={6}bars afterLossOnly={7}",
                  HtfTimeFrame, HtfEmaLength, HtfRequireStructure, LtfTimeFrame, LtfEmaLength,
                  BlockSameDirection, SameDirCooldownBars, OnlyBlockAfterLoss);

            Print("RISK: sizing={0} | spreadGuard {1}% of SL | killSwitch {2}% | BE {3} @ {4}R +{5}pips | trail {6} {7}xATR from {8}R | atrRelative={9}",
                  UsePercentRisk ? RiskPercent + "% equity (cap " + MaxLots + " lots)" : "fixed " + Lots + " lots",
                  MaxSpreadPctOfSl, MaxTotalDrawdown,
                  UseBreakeven, BreakevenTriggerR, BreakevenOffsetPips,
                  UseAtrTrail, TrailAtrMultiplier, TrailStartR, UseAtrRelative);

            if (UseFibSwingScore)
                Print("FIB SCORE ARM enabled: threshold {0:F2}, zone tolerance {1:P1} of range, shortBiasOnly={2}, stopBuffer={3}xATR.",
                      FibScoreThreshold, FibZoneTolerance, FibShortBiasOnly, FibSwingStopBufferAtr);

            if (UseHardCutoff)
                Print("HARD CUTOFF enabled: all positions flattened at {0:00}:00 UTC and no entries during that hour.", HardCutoffHour);

            if (UseNewsBlackout)
                Print("NEWS BLACKOUT enabled: no entries {0:00}:{1:00}-{2} UTC daily.",
                      NewsBlackoutHour, NewsBlackoutMinute,
                      TimeSpan.FromMinutes(NewsBlackoutHour * 60 + NewsBlackoutMinute + NewsBlackoutDuration).ToString(@"hh\:mm"));
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

        // Rebuilds today's trade count and realized PnL from trade history so a
        // bot restart cannot reset the per-day limits.
        private void RestoreDailyStateFromHistory()
        {
            DateTime today = Server.Time.Date;
            double realizedToday = 0;
            int entriesToday = 0;

            foreach (HistoricalTrade trade in History)
            {
                if (trade.Label != Label || trade.SymbolName != SymbolName)
                    continue;

                if (trade.EntryTime.Date == today)
                    entriesToday++;

                if (trade.ClosingTime.Date == today)
                    realizedToday += trade.NetProfit;
            }

            double floating = 0;

            foreach (var position in Positions.FindAll(Label, SymbolName))
            {
                if (position.EntryTime.Date == today)
                    entriesToday++;

                floating += position.NetProfit;
            }

            _tradesToday = entriesToday;
            _realizedToday = realizedToday;
            _floatingAtDayStart = 0;

            // Approximate the equity this bot's account had at the start of the
            // day by backing out today's bot PnL from current equity.
            _dailyStartEquity = Account.Equity - realizedToday - floating;

            if (entriesToday > 0)
                Print("RESTORED daily state from history: {0} trade(s) today, realized {1:F2}, baseline equity {2:F2}.",
                      entriesToday, realizedToday, _dailyStartEquity);
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (p.Label != Label || p.SymbolName != SymbolName)
                return;

            _lastClosedDirection = p.TradeType;
            _lastClosedBarIndex = Bars.Count - 1;
            _lastCloseWasLoss = p.NetProfit < 0;
            _realizedToday += p.NetProfit;
        }

        // Protective logic runs on every tick so exits do not wait for the next
        // bar open (important on H1/H4 charts).
        protected override void OnTick()
        {
            ResetDailyIfNewDay();
            UpdateDailyLossStatus();
            CheckKillSwitch();
            HandleHardCutoff();
            HandleSessionEnd();
            HandleMaxHoldTime();
            ManageOpenPositions();
        }

        protected override void OnBar()
        {
            ResetDailyIfNewDay();

            if (!ChartDataReady())
                return;

            // Swings/FVGs are tracked even while a position is open so the
            // structure state is never stale when the next entry is evaluated.
            UpdateSwings();
            UpdateFvgs();

            if (HasOpenPosition())
                return;

            if (!AllFiltersPass())
                return;

            EvaluateEntries();
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
                _realizedToday = 0;
                _dailyStartEquity = Account.Equity;
                _floatingAtDayStart = SumLabelFloatingPnl();
                _dailyLossHit = false;
            }
        }

        private double SumLabelFloatingPnl()
        {
            double total = 0;

            foreach (var position in Positions.FindAll(Label, SymbolName))
                total += position.NetProfit;

            return total;
        }

        // Daily loss is measured from THIS bot's trades only (realized today +
        // change in floating PnL), so other activity on the account does not
        // contaminate the limit.
        private void UpdateDailyLossStatus()
        {
            if (_dailyStartEquity <= 0)
                return;

            double dailyPnl = _realizedToday + SumLabelFloatingPnl() - _floatingAtDayStart;
            double dailyLossPct = -dailyPnl / _dailyStartEquity * 100.0;

            if (dailyLossPct >= MaxDailyLoss)
            {
                if (!_dailyLossHit)
                    Print("DAILY LOSS LIMIT HIT: {0:F2}% (bot PnL {1:F2}) - no new trades today.", dailyLossPct, dailyPnl);

                _dailyLossHit = true;
            }
        }

        private void CheckKillSwitch()
        {
            if (MaxTotalDrawdown <= 0 || _killSwitchHit || _startingEquity <= 0)
                return;

            double drawdownPct = (_startingEquity - Account.Equity) / _startingEquity * 100.0;

            if (drawdownPct < MaxTotalDrawdown)
                return;

            _killSwitchHit = true;
            Print("KILL SWITCH: total drawdown {0:F2}% >= {1}% - flattening and halting all trading.", drawdownPct, MaxTotalDrawdown);

            foreach (var position in Positions.FindAll(Label, SymbolName))
                ClosePosition(position);
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

        private bool IsInNewsBlackout()
        {
            if (!UseNewsBlackout)
                return false;

            int nowMinutes = Server.Time.Hour * 60 + Server.Time.Minute;
            int start = NewsBlackoutHour * 60 + NewsBlackoutMinute;
            int end = (start + NewsBlackoutDuration) % 1440;

            if (start < end)
                return nowMinutes >= start && nowMinutes < end;

            return nowMinutes >= start || nowMinutes < end;
        }

        private void HandleHardCutoff()
        {
            if (!UseHardCutoff)
                return;

            // Most recent cutoff moment at or before the current server time.
            DateTime cutoff = Server.Time.Date.AddHours(HardCutoffHour);

            if (Server.Time < cutoff)
                cutoff = cutoff.AddDays(-1);

            foreach (var position in Positions.FindAll(Label, SymbolName))
            {
                if (position.EntryTime < cutoff)
                {
                    ClosePosition(position);
                    Print("HARD CUTOFF {0:00}:00 UTC - position force-closed. ID {1} Net {2:F2}",
                          HardCutoffHour, position.Id, position.NetProfit);
                }
            }
        }

        private void HandleSessionEnd()
        {
            if (!CloseAtSessionEnd || !UseSessionFilter)
                return;

            if (IsInSession())
                return;

            foreach (var position in Positions.FindAll(Label, SymbolName))
            {
                ClosePosition(position);
                Print("Position closed outside session. ID {0}", position.Id);
            }
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

                    ClosePosition(position);
                    Print("MAX-HOLD exit {0:F1}h. ID {1} Net {2:F2}", hoursOpen, position.Id, position.NetProfit);
                    continue;
                }

                if (SoftLossCutMoney > 0 &&
                    hoursOpen >= SoftCutAfterHours &&
                    position.NetProfit <= -SoftLossCutMoney)
                {
                    ClosePosition(position);
                    Print("SOFT-LOSS cut after {0:F1}h. ID {1} Net {2:F2}", hoursOpen, position.Id, position.NetProfit);
                }
            }
        }

        // Breakeven + optional ATR trailing. The initial risk distance is read
        // back from the position comment ("R=<sl pips>") so it survives
        // restarts and SL modifications.
        private void ManageOpenPositions()
        {
            if (!UseBreakeven && !UseAtrTrail)
                return;

            double atrValue = _atr.Result.Count > AtrLength ? _atr.Result.Last(ClosedBarOffset) : double.NaN;

            foreach (var position in Positions.FindAll(Label, SymbolName))
            {
                double riskDistance = GetInitialRiskDistance(position);

                if (riskDistance <= 0)
                    continue;

                double entry = position.EntryPrice;

                if (position.TradeType == TradeType.Buy)
                {
                    double profitDistance = Symbol.Bid - entry;

                    if (UseBreakeven && profitDistance >= riskDistance * BreakevenTriggerR)
                    {
                        double bePrice = entry + BreakevenOffsetPips * Symbol.PipSize;

                        if ((!position.StopLoss.HasValue || position.StopLoss.Value < bePrice - Symbol.TickSize) &&
                            bePrice < Symbol.Bid)
                        {
                            position.ModifyStopLossPrice(bePrice);
                            Print("BREAKEVEN set at {0} (+{1}R reached). ID {2}", bePrice, BreakevenTriggerR, position.Id);
                        }
                    }

                    if (UseAtrTrail && !double.IsNaN(atrValue) && atrValue > 0 &&
                        profitDistance >= riskDistance * TrailStartR)
                    {
                        double trailSl = Symbol.Bid - atrValue * TrailAtrMultiplier;

                        if ((!position.StopLoss.HasValue || trailSl > position.StopLoss.Value + Symbol.TickSize) &&
                            trailSl < Symbol.Bid)
                            position.ModifyStopLossPrice(trailSl);
                    }
                }
                else
                {
                    double profitDistance = entry - Symbol.Ask;

                    if (UseBreakeven && profitDistance >= riskDistance * BreakevenTriggerR)
                    {
                        double bePrice = entry - BreakevenOffsetPips * Symbol.PipSize;

                        if ((!position.StopLoss.HasValue || position.StopLoss.Value > bePrice + Symbol.TickSize) &&
                            bePrice > Symbol.Ask)
                        {
                            position.ModifyStopLossPrice(bePrice);
                            Print("BREAKEVEN set at {0} (+{1}R reached). ID {2}", bePrice, BreakevenTriggerR, position.Id);
                        }
                    }

                    if (UseAtrTrail && !double.IsNaN(atrValue) && atrValue > 0 &&
                        profitDistance >= riskDistance * TrailStartR)
                    {
                        double trailSl = Symbol.Ask + atrValue * TrailAtrMultiplier;

                        if ((!position.StopLoss.HasValue || trailSl < position.StopLoss.Value - Symbol.TickSize) &&
                            trailSl > Symbol.Ask)
                            position.ModifyStopLossPrice(trailSl);
                    }
                }
            }
        }

        private double GetInitialRiskDistance(Position position)
        {
            string comment = position.Comment;

            if (string.IsNullOrEmpty(comment) || !comment.StartsWith(RiskCommentPrefix, StringComparison.Ordinal))
                return 0;

            double slPips;

            if (!double.TryParse(comment.Substring(RiskCommentPrefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out slPips))
                return 0;

            return slPips * Symbol.PipSize;
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
            int pivotBarIndex = CurrentClosedBarIndex - StructureLen;

            if (!double.IsNaN(pivotHigh))
            {
                _lastSwingHigh2 = _lastSwingHigh1;
                _lastSwingHighBar2 = _lastSwingHighBar1;
                _lastSwingHigh1 = pivotHigh;
                _lastSwingHighBar1 = pivotBarIndex;
            }

            double pivotLow = DetectPivotLow(StructureLen);

            if (!double.IsNaN(pivotLow))
            {
                _lastSwingLow2 = _lastSwingLow1;
                _lastSwingLowBar2 = _lastSwingLowBar1;
                _lastSwingLow1 = pivotLow;
                _lastSwingLowBar1 = pivotBarIndex;
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

        private double EffectiveFvgMinSize()
        {
            if (UseAtrRelative)
            {
                double atrValue = _atr.Result.Last(ClosedBarOffset);

                if (!double.IsNaN(atrValue) && atrValue > 0)
                    return atrValue * FvgMinSizeAtr;
            }

            return FvgMinSize;
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

            double minSize = EffectiveFvgMinSize();

            double highTwoBarsBack = Bars.HighPrices.Last(3);
            double lowCurrentClosed = Bars.LowPrices.Last(ClosedBarOffset);

            if (highTwoBarsBack < lowCurrentClosed && (lowCurrentClosed - highTwoBarsBack) >= minSize)
            {
                _bullFvgTop = lowCurrentClosed;
                _bullFvgBottom = highTwoBarsBack;
                _bullFvgBar = completedBarIndex;
            }

            double lowTwoBarsBack = Bars.LowPrices.Last(3);
            double highCurrentClosed = Bars.HighPrices.Last(ClosedBarOffset);

            if (lowTwoBarsBack > highCurrentClosed && (lowTwoBarsBack - highCurrentClosed) >= minSize)
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
            if (_killSwitchHit)
                return false;

            if (_tradesToday >= MaxTradesPerDay)
                return false;

            if (_dailyLossHit)
                return false;

            if (IsInHardCutoffHour())
                return false;

            if (IsInNewsBlackout())
                return false;

            if (UseSessionFilter && !IsInSession())
                return false;

            if (UseDayFilter)
            {
                DayOfWeek today = Server.Time.DayOfWeek;

                bool dayAllowed = (today == DayOfWeek.Monday && TradeMon) ||
                                  (today == DayOfWeek.Tuesday && TradeTue) ||
                                  (today == DayOfWeek.Wednesday && TradeWed) ||
                                  (today == DayOfWeek.Thursday && TradeThu) ||
                                  (today == DayOfWeek.Friday && TradeFri);

                if (!dayAllowed)
                    return false;
            }

            if (BlockEarlyMonth && Server.Time.Day <= 5)
                return false;

            return true;
        }

        private void EvaluateEntries()
        {
            if (UseFibSwingScore)
            {
                EvaluateFibScoreEntry();
                return;
            }

            double close = Bars.ClosePrices.Last(ClosedBarOffset);
            double high = Bars.HighPrices.Last(ClosedBarOffset);
            double low = Bars.LowPrices.Last(ClosedBarOffset);
            double ema = _ema.Result.Last(ClosedBarOffset);
            double atrValue = _atr.Result.Last(ClosedBarOffset);

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

            // Pullback tolerance: ATR-relative adapts to volatility; legacy mode
            // keeps the original 0.2%-of-price band.
            double pullbackTolerance = UseAtrRelative && !double.IsNaN(atrValue) && atrValue > 0
                ? atrValue * PullbackAtrTolerance
                : ema * 0.002;

            bool pullbackToBuy = bullishStructure && low <= ema + pullbackTolerance && close > ema;
            bool pullbackToSell = bearishStructure && high >= ema - pullbackTolerance && close < ema;

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

        private void EvaluateFibScoreEntry()
        {
            double close = Bars.ClosePrices.Last(ClosedBarOffset);
            double high = Bars.HighPrices.Last(ClosedBarOffset);
            double low = Bars.LowPrices.Last(ClosedBarOffset);
            int currentBarIndex = CurrentClosedBarIndex;

            double swingHigh;
            double swingLow;
            TradeType direction;
            int pivotBar;

            if (!TryGetLastSwing(StructureLen, out swingHigh, out swingLow, out direction, out pivotBar))
                return;

            if (FibShortBiasOnly && direction != TradeType.Sell)
                return;

            double range = swingHigh - swingLow;

            if (range <= Symbol.TickSize)
                return;

            bool fvg = HasDirectionalFvgConfluence(direction, currentBarIndex, high, low, close);
            double priceScore = CalculateFibPriceScore(close, swingHigh, swingLow, direction);
            double timeScore = CalculateFibTimeScore(currentBarIndex - pivotBar);
            double score = FibPriceScoreWeight * priceScore +
                           FibTimeScoreWeight * timeScore +
                           FibFvgScoreWeight * (fvg ? 1.0 : 0.0);

            if (score < FibScoreThreshold)
                return;

            _signalsSeen++;

            if (!DirectionalEntryFiltersPass(direction))
                return;

            double slPips;
            double tpPips;

            if (!TryBuildFibExitPlan(direction, close, swingHigh, swingLow, out slPips, out tpPips))
            {
                if (DiagnosticMode)
                    Print("{0} FibScore skipped - invalid swing stop/extension target. score={1:F2} hi={2} lo={3}",
                          direction, score, swingHigh, swingLow);

                return;
            }

            if (ExecuteEntry(direction, slPips, tpPips, "FibScore"))
            {
                if (direction == TradeType.Buy && fvg)
                    ClearBullFvg();
                else if (direction == TradeType.Sell && fvg)
                    ClearBearFvg();

                Print("{0} FibScore armed: score={1:F2} price={2:F2} time={3:F2} fvg={4} pivotAge={5} SL={6:F1} TP={7:F1}",
                      direction, score, priceScore, timeScore, fvg, currentBarIndex - pivotBar, slPips, tpPips);
            }
        }

        private bool TryGetLastSwing(int lookback, out double swingHigh, out double swingLow, out TradeType direction, out int pivotBar)
        {
            swingHigh = double.NaN;
            swingLow = double.NaN;
            direction = TradeType.Sell;
            pivotBar = -1;

            if (lookback <= 0 ||
                double.IsNaN(_lastSwingHigh1) ||
                double.IsNaN(_lastSwingLow1) ||
                _lastSwingHighBar1 < 0 ||
                _lastSwingLowBar1 < 0)
                return false;

            if (_lastSwingLowBar1 > _lastSwingHighBar1)
            {
                swingHigh = _lastSwingHigh1;
                swingLow = _lastSwingLow1;
                direction = TradeType.Sell;
                pivotBar = _lastSwingLowBar1;
                return swingHigh > swingLow;
            }

            if (_lastSwingHighBar1 > _lastSwingLowBar1)
            {
                swingHigh = _lastSwingHigh1;
                swingLow = _lastSwingLow1;
                direction = TradeType.Buy;
                pivotBar = _lastSwingHighBar1;
                return swingHigh > swingLow;
            }

            return false;
        }

        private double CalculateFibPriceScore(double close, double swingHigh, double swingLow, TradeType direction)
        {
            double range = swingHigh - swingLow;
            double toleranceDistance = range * FibZoneTolerance;

            if (range <= 0 || toleranceDistance <= 0)
                return 0;

            double minDistance = double.MaxValue;

            foreach (double retracement in FibRetracements)
            {
                double zone = direction == TradeType.Sell
                    ? swingHigh - range * retracement
                    : swingLow + range * retracement;

                minDistance = Math.Min(minDistance, Math.Abs(close - zone));
            }

            return Clamp01(1.0 - minDistance / toleranceDistance);
        }

        private double CalculateFibTimeScore(int barsSincePivot)
        {
            if (barsSincePivot < 0)
                return 0;

            foreach (int target in FibTimeCounts)
            {
                if (Math.Abs(barsSincePivot - target) <= 1)
                    return 1;
            }

            return 0;
        }

        private bool HasDirectionalFvgConfluence(TradeType direction, int currentBarIndex, double high, double low, double close)
        {
            if (direction == TradeType.Buy)
            {
                int age = _bullFvgBar >= 0 ? currentBarIndex - _bullFvgBar : -1;
                bool valid = _bullFvgBar >= 0 &&
                             age >= 0 &&
                             age <= FvgMaxAge &&
                             low > _bullFvgBottom;

                return valid &&
                       low <= _bullFvgTop &&
                       close >= _bullFvgBottom;
            }

            int bearAge = _bearFvgBar >= 0 ? currentBarIndex - _bearFvgBar : -1;
            bool bearValid = _bearFvgBar >= 0 &&
                             bearAge >= 0 &&
                             bearAge <= FvgMaxAge &&
                             high < _bearFvgTop;

            return bearValid &&
                   high >= _bearFvgBottom &&
                   close <= _bearFvgTop;
        }

        private bool DirectionalEntryFiltersPass(TradeType direction)
        {
            if (direction == TradeType.Buy)
            {
                if (!HtfAllowsLong())
                {
                    _htfBlocks++;

                    if (DiagnosticMode)
                        Print("LONG skipped - HTF bias not bullish or not ready ({0}).", HtfTimeFrame);

                    return false;
                }

                if (!LtfConfirmsLong())
                {
                    _ltfBlocks++;

                    if (DiagnosticMode)
                        Print("LONG skipped - LTF momentum not confirming or not ready ({0}).", LtfTimeFrame);

                    return false;
                }
            }
            else
            {
                if (!HtfAllowsShort())
                {
                    _htfBlocks++;

                    if (DiagnosticMode)
                        Print("SHORT skipped - HTF bias not bearish or not ready ({0}).", HtfTimeFrame);

                    return false;
                }

                if (!LtfConfirmsShort())
                {
                    _ltfBlocks++;

                    if (DiagnosticMode)
                        Print("SHORT skipped - LTF momentum not confirming or not ready ({0}).", LtfTimeFrame);

                    return false;
                }
            }

            if (IsSameDirectionBlocked(direction))
            {
                _reentryBlocks++;

                if (DiagnosticMode)
                    Print("{0} skipped - same-direction cooldown active ({1} bars).", direction, SameDirCooldownBars);

                return false;
            }

            return true;
        }

        private bool TryBuildFibExitPlan(TradeType direction, double close, double swingHigh, double swingLow, out double slPips, out double tpPips)
        {
            slPips = 0;
            tpPips = 0;

            double range = swingHigh - swingLow;

            if (range <= Symbol.TickSize)
                return false;

            double atrValue = _atr.Result.Last(ClosedBarOffset);
            double buffer = Symbol.PipSize;

            if (!double.IsNaN(atrValue) && atrValue > 0 && FibSwingStopBufferAtr > 0)
                buffer = Math.Max(buffer, atrValue * FibSwingStopBufferAtr);

            double entry = direction == TradeType.Buy ? Symbol.Ask : Symbol.Bid;

            if (entry <= 0)
                entry = close;

            double stop;
            double target;

            if (direction == TradeType.Buy)
            {
                stop = swingLow - buffer;
                target = SelectFibExtensionTarget(direction, entry, swingHigh, swingLow);

                if (stop >= entry - Symbol.TickSize || target <= entry + Symbol.TickSize)
                    return false;

                slPips = (entry - stop) / Symbol.PipSize;
                tpPips = (target - entry) / Symbol.PipSize;
            }
            else
            {
                stop = swingHigh + buffer;
                target = SelectFibExtensionTarget(direction, entry, swingHigh, swingLow);

                if (stop <= entry + Symbol.TickSize || target >= entry - Symbol.TickSize)
                    return false;

                slPips = (stop - entry) / Symbol.PipSize;
                tpPips = (entry - target) / Symbol.PipSize;
            }

            return slPips > 0 && tpPips > 0;
        }

        private double SelectFibExtensionTarget(TradeType direction, double entry, double swingHigh, double swingLow)
        {
            double range = swingHigh - swingLow;

            if (direction == TradeType.Buy)
            {
                double extension1272 = swingLow + range * FibExtension1272;

                if (extension1272 > entry + Symbol.TickSize)
                    return extension1272;

                return swingLow + range * FibExtension1618;
            }

            double shortExtension1272 = swingHigh - range * FibExtension1272;

            if (shortExtension1272 < entry - Symbol.TickSize)
                return shortExtension1272;

            return swingHigh - range * FibExtension1618;
        }

        private double Clamp01(double value)
        {
            if (value < 0)
                return 0;

            if (value > 1)
                return 1;

            return value;
        }

        // Sizes the position from a fixed % of equity so the dollar risk per
        // trade stays constant even though the ATR stop distance varies.
        private double ComputeVolumeInUnits(double slPips)
        {
            double volumeUnits;

            if (UsePercentRisk)
            {
                double riskAmount = Account.Equity * RiskPercent / 100.0;
                double riskPerUnit = slPips * Symbol.PipValue;

                if (riskPerUnit <= 0)
                    return 0;

                volumeUnits = riskAmount / riskPerUnit;

                double capUnits = Symbol.QuantityToVolumeInUnits(MaxLots);
                volumeUnits = Math.Min(volumeUnits, capUnits);
            }
            else
            {
                volumeUnits = Symbol.QuantityToVolumeInUnits(Lots);
            }

            volumeUnits = Symbol.NormalizeVolumeInUnits(volumeUnits, RoundingMode.Down);

            if (volumeUnits < Symbol.VolumeInUnitsMin)
                return 0;

            return volumeUnits;
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

            return ExecuteEntry(direction, slPips, tpPips, "ATR");
        }

        private bool ExecuteEntry(TradeType direction, double slPips, double tpPips, string setupName)
        {
            if (slPips <= 0 || tpPips <= 0)
            {
                Print("{0} entry skipped - invalid SL/TP distances. SL {1:F1} pips TP {2:F1} pips.",
                      setupName, slPips, tpPips);
                return false;
            }

            double slDistance = slPips * Symbol.PipSize;

            if (MaxSpreadPctOfSl > 0)
            {
                double spreadPct = Symbol.Spread / slDistance * 100.0;

                if (spreadPct > MaxSpreadPctOfSl)
                {
                    Print("{0} entry skipped - spread {1:F1}% of SL exceeds {2}% limit.", setupName, spreadPct, MaxSpreadPctOfSl);
                    return false;
                }
            }

            double volume = ComputeVolumeInUnits(slPips);

            if (volume <= 0)
            {
                Print("Calculated volume below symbol minimum - skipping entry. Check risk settings vs SL distance.");
                return false;
            }

            string comment = RiskCommentPrefix + slPips.ToString("F1", CultureInfo.InvariantCulture);

            var result = ExecuteMarketOrder(direction, SymbolName, volume, Label, slPips, tpPips, comment);

            if (!result.IsSuccessful)
            {
                Print("Order failed: {0}", result.Error);
                return false;
            }

            _tradesToday++;
            _tradesFired++;

            // Never run a position without a stop: if the broker rejected the
            // SL (e.g. min stop distance), bail out immediately.
            var position = result.Position;

            if (position == null || !position.StopLoss.HasValue)
            {
                Print("CRITICAL: stop loss missing after fill - closing naked position immediately.");

                if (position != null)
                    ClosePosition(position);

                return false;
            }

            Print("{0} {1} entry filled. Volume {2} ({3:F2} lots) SL {4:F1} pips TP {5:F1} pips Trades {6}/{7}",
                  setupName, direction, volume, Symbol.VolumeInUnitsToQuantity(volume), slPips, tpPips, _tradesToday, MaxTradesPerDay);

            return true;
        }
    }
}

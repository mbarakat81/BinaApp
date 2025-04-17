using System.Collections.Generic;

namespace TradingBot.Models
{
    public class MarketCondition
    {
        public bool IsFavorable { get; set; }
        public string Reason { get; set; }
        public decimal BuyThreshold { get; set; } = 0.015m;
        public decimal TakeProfitPercent { get; set; } = 0.2m;
        public decimal StopLossPercent { get; set; } = 0.4m;
        public bool IsVolatile { get; set; } = false;
        public bool IsStrongTrend { get; set; }
        public TrendDirection TrendDirection { get; set; }
        public decimal RSI { get; set; }
        public bool MacdBullish { get; set; }
        public List<KeyPriceLevel> SupportLevels { get; set; } = new List<KeyPriceLevel>();
        public List<KeyPriceLevel> ResistanceLevels { get; set; } = new List<KeyPriceLevel>();
    }

    public enum TrendDirection
    {
        Up,
        Down,
        Sideways
    }
}

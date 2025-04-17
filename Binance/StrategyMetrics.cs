using System.Collections.Generic;

namespace TradingBot.Models
{
    public class StrategyMetrics
    {
        public int TotalTrades { get; set; }
        public int ProfitableTrades { get; set; }
        public decimal TotalProfit { get; set; }
        public decimal MaxProfit { get; set; }
        public decimal MaxLoss { get; set; }
        public decimal AvgProfit { get; set; }
        public decimal WinRate { get; set; }
        public Dictionary<string, decimal> Parameters { get; set; } = new Dictionary<string, decimal>();
    }
}

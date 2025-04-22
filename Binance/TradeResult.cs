using System;

namespace TradingBot.Models
{
    public class TradeResult
    {
        public bool Success { get; set; }
        public decimal Profit { get; set; }
        public string ErrorMessage { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal Quantity { get; set; }
        public DateTime EntryTime { get; set; }
        public DateTime ExitTime { get; set; }
        public string ExitReason { get; set; }
        public string Symbol { get; set; }
        public bool ShouldReplaceSymbol { get; set; }
        public bool CanDeprioritize { get; set; }
        public decimal ReceivedAmount { get; set; }
        public string Message { get; set; }
    }
}

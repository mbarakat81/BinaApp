namespace TradingBot.Models
{
    public class CoinData
    {
        public string Symbol { get; set; }
        public decimal Volume { get; set; }
        public decimal HourlyVolatility { get; set; }
        public decimal MinuteVolatility { get; set; }
        public decimal BidAskSpread { get; set; }
        public decimal LiquidityDepth { get; set; }
        public decimal Momentum { get; set; }
        public decimal BtcCorrelation { get; set; }
        public decimal Sentiment { get; set; }
        public long TradeCount { get; set; }
        public decimal PriceChangePercent { get; set; }
        public decimal Score { get; set; }
    }
}

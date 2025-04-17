using Binance.Net.Clients;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace TradingBot.Analyzers
{
    public enum OrderBookSignal
    {
        StrongBuy,
        Buy,
        Neutral,
        Sell,
        StrongSell
    }

    public static class OrderBookAnalyzer
    {
        public static async Task<OrderBookSignal> AnalyzeOrderBook(
            BinanceRestClient client,
            string symbol)
        {
            try
            {
                // Get order book
                var orderBook = await client.SpotApi.ExchangeData.GetOrderBookAsync(symbol, 20);
                if (!orderBook.Success)
                    return OrderBookSignal.Neutral;

                // Calculate total volume at each side
                decimal bidVolume = orderBook.Data.Bids.Sum(b => b.Quantity * b.Price);
                decimal askVolume = orderBook.Data.Asks.Sum(a => a.Quantity * a.Price);

                // Calculate imbalance ratio
                decimal imbalanceRatio = bidVolume / (bidVolume + askVolume);

                // Check for large orders (walls)
                bool hasBidWall = orderBook.Data.Bids.Any(b => b.Quantity * b.Price > bidVolume * 0.2m);
                bool hasAskWall = orderBook.Data.Asks.Any(a => a.Quantity * a.Price > askVolume * 0.2m);

                // Calculate bid-ask spread
                decimal topBid = orderBook.Data.Bids.First().Price;
                decimal topAsk = orderBook.Data.Asks.First().Price;
                decimal spread = (topAsk - topBid) / topBid;

                // Analyze first 5 levels for depth
                decimal top5BidVolume = orderBook.Data.Bids.Take(5).Sum(b => b.Quantity * b.Price);
                decimal top5AskVolume = orderBook.Data.Asks.Take(5).Sum(a => a.Quantity * a.Price);

                // Determine signal
                if (imbalanceRatio > 0.65m && !hasAskWall)
                    return OrderBookSignal.StrongBuy;
                else if (imbalanceRatio > 0.55m && top5BidVolume > top5AskVolume)
                    return OrderBookSignal.Buy;
                else if (imbalanceRatio < 0.35m && !hasBidWall)
                    return OrderBookSignal.StrongSell;
                else if (imbalanceRatio < 0.45m && top5BidVolume < top5AskVolume)
                    return OrderBookSignal.Sell;
                else
                    return OrderBookSignal.Neutral;
            }
            catch
            {
                return OrderBookSignal.Neutral;
            }
        }
    }
}

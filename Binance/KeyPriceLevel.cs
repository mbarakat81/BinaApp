namespace TradingBot.Models
{
    public class KeyPriceLevel
    {
        public decimal Price { get; set; }
        public decimal Strength { get; set; }
        public string Type { get; set; }  // Support or Resistance
    }
}

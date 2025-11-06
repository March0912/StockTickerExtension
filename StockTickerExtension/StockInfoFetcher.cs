using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using static System.Net.WebRequestMethods;

namespace StockTickerExtension
{
    internal class StockInfoFetcher
    {
        public partial class StockCurrentInfo
        {
            public string Name;
            public double Price;
            public double Open;
            public double High;
            public double Low;
            public double Change;
        }

        static public async Task<StockCurrentInfo> FetchStockInfoAsync(string code, StockMarket stockType = StockMarket.StockA)
        {
            try
            {
                var secid = Tool.GetSecId(stockType, code);
                string requestUrl = $"https://push2.eastmoney.com/api/qt/stock/get?secid={secid}&fields=f58,f43,f46,f44,f45,f170";

                HttpClient httpClient = new HttpClient();
                var text = await httpClient.GetStringAsync(requestUrl);
                var jobj = JObject.Parse(text);
                if (jobj["data"] == null)
                    return null;

                var dataObj = jobj["data"];
                if (dataObj.Type == JTokenType.Null)
                    return null;

                string name = dataObj["f58"].ToObject<string>();
                double price = dataObj["f43"].ToObject<double>() / 100.0;
                double high = dataObj["f44"].ToObject<double>() / 100.0;
                double low = dataObj["f45"].ToObject<double>() / 100.0;
                double open = dataObj["f46"].ToObject<double>() / 100.0;
                double change = dataObj["f170"].ToObject<double>() / 100.0;

                return new StockCurrentInfo()
                {
                    Name = name,
                    Price = price,
                    Open = open,
                    High = high,
                    Low = low,
                    Change = change
                };
            }
            catch
            {
                return null;
            }
        }
    }
}

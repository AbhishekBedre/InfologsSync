using Infologs.SessionReader;
using Microsoft.EntityFrameworkCore;
using OptionChain;
using System.Text;
using System.Text.RegularExpressions;

namespace SyncData
{
    public enum JobType
    {
        SessionUpdate,
        NiftyUpdate,
        BroderMarketUpdate,
        StockUpdate,
        BankNiftyUpdate,
        FIIDIIActivity
    }

    public static class Common
    {
        public static async Task UpdateCookieAndHeaders(HttpClient httpClient, OptionDbContext optionDbContext, JobType jobType, ICacheHelper cacheHelper)
        {
            var sessionCookie = "";
            var cookies = "";

            var Cookie = cacheHelper.Get<string>("sessionCookie");

            if (string.IsNullOrWhiteSpace(Cookie))
            {
                var sessionInfo = await optionDbContext.Sessions.FirstAsync();

                if (sessionInfo != null)
                {
                    sessionCookie = sessionInfo.Cookie ?? "";
                }
            }
            else
            {
                sessionCookie = Cookie;
            }

            foreach (var cookie in sessionCookie.Split(";"))
            {
                if (cookie.Trim().StartsWith("_abck="))
                {
                    cookies += cookie.Trim() + ";";
                }

                if (cookie.Trim().StartsWith("ak_bmsc="))
                {
                    cookies += cookie.Trim() + ";";
                }

                if (cookie.Trim().StartsWith("bm_sv="))
                {
                    cookies += cookie.Trim() + ";";
                }

                if (cookie.Trim().StartsWith("bm_sz="))
                {
                    cookies += cookie.Trim() + ";";
                }

                if (cookie.Trim().StartsWith("nseappid="))
                {
                    cookies += cookie.Trim() + ";";
                }

                if (cookie.Trim().StartsWith("nsit="))
                {
                    cookies += cookie.Trim() + ";";
                }
            }
            cookies += "AKA_A2=A;";
            cookies = cookies.Trim();

            if (jobType == JobType.BroderMarketUpdate)
            {
                string pattern = @"(nsit=[^;]*;|nseappid=[^;]*;)";

                // Replace matched substrings with an empty string
                cookies = Regex.Replace(cookies, pattern, string.Empty);
            }

            httpClient.DefaultRequestHeaders.Add("User-Agent", "PostmanRuntime/7.43.0");
            httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
            httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
            httpClient.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            httpClient.DefaultRequestHeaders.Add("cookie", cookies);
        }
    }
}
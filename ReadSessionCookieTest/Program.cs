
string cookie = await getSessionCookie();

Console.WriteLine(cookie);

Console.ReadLine();

async Task<string> getSessionCookie() {

    string finalCookie = "";
    string url = "https://www.nseindia.com/";

    HttpClientHandler httpClientHandler = new HttpClientHandler();

    // Enable automatic decompression for gzip, deflate, and Brotli
    httpClientHandler.AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate |
                                     System.Net.DecompressionMethods.Brotli;

    using (HttpClient client = new HttpClient(httpClientHandler))
    {
        client.DefaultRequestHeaders.Add("User-Agent", "PostmanRuntime/7.43.0");
        client.DefaultRequestHeaders.Add("Accept", "*/*");
        client.DefaultRequestHeaders.Add("Connection", "keep-alive");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        client.DefaultRequestHeaders.Add("cookie", "AKA_A2=A;");

        try
        {
            HttpResponseMessage response = await client.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                string localCookie = response.Headers.NonValidated.ToList().Where(x => x.Key == "Set-Cookie").FirstOrDefault().Value.ToString();

                foreach (var cookie in localCookie.Split(";"))
                {
                    if (cookie.Trim().Contains("_abck="))
                        finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("_abck=")).Trim() + ";";

                    if (cookie.Trim().Contains("ak_bmsc"))
                        finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("ak_bmsc=")).Trim() + ";";

                    if (cookie.Trim().Contains("bm_sv="))
                        finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("bm_sv=")).Trim() + ";";

                    if (cookie.Trim().Contains("bm_sz="))
                        finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("bm_sz=")).Trim() + ";";

                    if (cookie.Trim().Contains("nseappid="))
                        finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("nseappid=")).Trim() + ";";

                    if (cookie.Trim().Contains("nsit="))
                        finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("nsit=")).Trim() + ";";

                    if (cookie.Trim().Contains("AKA_A2="))
                        finalCookie += cookie.Trim().Substring(cookie.Trim().IndexOf("AKA_A2=")).Trim() + ";";
                }

                finalCookie = finalCookie.Trim();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return "";
        }
    }

    return finalCookie;

}
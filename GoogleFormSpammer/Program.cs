using GoogleFormSpammer;
using Newtonsoft.Json;
using StackExchange.Redis;
using System.Net;
using System.Net.Http.Headers;

class Program
{
    public string data;
    public string code;

    public string proxyUrl;

    public string confURL;
    public string confData;
    public string confCaptchPageData;
    public string confPhrase;
    public int confMode;

    public int numOfRequestsBeforeCheck = 200;

    public string unauthorized = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "unauthorized.txt");
    public string badRequest = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "badrequest.txt");
    public string formClosed = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "formclosed.txt");

    public int badRequestCounter = 0;

    public ConnectionMultiplexer redis;

    public static async Task Main(string[] args)
    {
        Program p = new Program();
        var configFile = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config.JSON")));

        p.confURL = configFile["url"];
        p.confData = configFile["data"];
        p.confCaptchPageData = configFile.ContainsKey("captchPageData") ? configFile["captchPageData"] : null;
        p.confMode = Convert.ToInt32(configFile["mode"]);
        p.confPhrase = configFile["phrase"];
        p.data = configFile["data"];

        p.redis = ConnectionMultiplexer.Connect(
            new ConfigurationOptions
            {
                EndPoints = { "" } //redis IP
            });

        if (File.Exists(p.badRequest))
            throw new Exception("Fix Request");

        if (File.Exists(p.formClosed))
            File.Delete(p.formClosed);

        if (File.Exists(p.unauthorized))
            File.Delete(p.unauthorized);

        await p.GetNewProxy();

        if (p.confMode == 1) //no code
        {
            await p.Spam(0);
        }
        else //code
        {
            await p.GetCode();
            await p.Spam(0);
        }
    }

    private async Task<HttpStatusCode> SpamTest()
    {
        HttpClientHandler handler = CreateHandler();

        HttpResponseMessage response = null;
        HttpStatusCode responseCode = HttpStatusCode.Created;

        var retry = false;

        using (var httpClient = new HttpClient(handler, true))
        {
            using (var request = new HttpRequestMessage(new HttpMethod("POST"), confURL + "/formResponse"))
            {
                PrepareRequest(data, request);
                try
                {
                    response = await httpClient.SendAsync(request);
                    responseCode = response.StatusCode;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Bad Proxy " + ex.Message.ToString());
                    retry = true;
                }
            }
        }

        if (retry ||
            responseCode == HttpStatusCode.TooManyRequests ||
            responseCode == HttpStatusCode.Forbidden ||
            responseCode == HttpStatusCode.Gone)
        {
            if (!retry)
                Console.WriteLine("Bad Proxy: SpamTest: " + responseCode.ToString());
            await GetNewProxy();
            return await SpamTest();
        }

        if (responseCode == HttpStatusCode.Unauthorized)
        {
            var responseHTML = await response.Content.ReadAsStringAsync();
            await File.WriteAllTextAsync(unauthorized, "SpamTest Failed: " + responseHTML);
            throw new Exception("UnAuthorized");
        }

        if (!retry)
            Console.WriteLine(responseCode + " " + proxyUrl);

        if ((responseCode == HttpStatusCode.BadRequest && confMode == 1) || badRequestCounter == 10) //not code
        {
            var responseHTML = await response.Content.ReadAsStringAsync();
            if (responseHTML.Contains("data-validation-failed=\"true\"") && !File.Exists(badRequest))
            {
                await File.WriteAllTextAsync(badRequest, "SpamTest Failed: " + responseHTML);
                throw new Exception("Bad Request");
            }
        }

        if (responseCode == HttpStatusCode.OK)
        {
            var responseHTML = await response.Content.ReadAsStringAsync();
            if (responseHTML.Contains("closedform"))
            {
                File.Create(formClosed);
                throw new Exception("Form Closed");
            }
            else if (!responseHTML.Contains("Google Docs"))
            {
                Console.WriteLine("Bad Proxy: SpamTest: response does not contain google docs");
                await GetNewProxy();
                return await SpamTest();
            }
        }

        return responseCode;
    }

    private async Task Spam(int numOfRequests)
    {
        GenerateWord();
        if (numOfRequests == 0)
        {
            var testResponse = await SpamTest();
            if (testResponse == HttpStatusCode.OK)
            {
                badRequestCounter = 0;
                await Spam(++numOfRequests);
            }
            else if (testResponse == HttpStatusCode.BadRequest && confMode == 2) //code
            {
                await GetCode();
                badRequestCounter++;
                await Spam(0);
            }
            else
            {
                Console.WriteLine("Test Failed");
                await Spam(0);
            }
        }
        else
        {
            var t = new Thread(SendRequestBackground);
            t.Start(data);

            if (numOfRequests >= numOfRequestsBeforeCheck)
                numOfRequests = -1;

            await Spam(++numOfRequests);
        }
    }

    private void GenerateWord()
    {
        var word = Guid.NewGuid().ToString().Replace("-", "");
        data = confData.Replace("ReplaceCode", code);
        data = data.Replace("ReplaceField", word);
    }

    private void SendRequestBackground(object? obj)
    {
        HttpClientHandler handler = CreateHandler();

        using (var httpClient = new HttpClient(handler, true))
        {
            using (var request = new HttpRequestMessage(new HttpMethod("POST"), confURL + "/formResponse"))
            {
                try
                {
                    PrepareRequest((string)obj, request);
                    httpClient.Send(request);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private async Task GetNewProxy()
    {
        var db = redis.GetDatabase();
        var proxiesJSON = await db.StringGetAsync("proxies");
        List<Proxies> proxyList = JsonConvert.DeserializeObject<List<Proxies>>(proxiesJSON);

        var random = new Random();
        var index = random.Next(proxyList.Count);

        var ip = proxyList[index].IP;
        var port = proxyList[index].PORT;

        proxyUrl = $"http://{ip}:{port}";
    }

    private HttpClientHandler CreateHandler()
    {
        var proxy = new WebProxy
        {
            Address = new Uri(proxyUrl),
            BypassProxyOnLocal = false,
            UseDefaultCredentials = false,
        };

        var handler = new HttpClientHandler
        {
            Proxy = proxy,
        };

        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        handler.AutomaticDecompression = ~DecompressionMethods.None;
        return handler;
    }

    private void PrepareRequest(string data, HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("authority", "docs.google.com");
        request.Headers.TryAddWithoutValidation("accept", "*/*");
        request.Headers.TryAddWithoutValidation("accept-language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("dnt", "1");
        request.Headers.TryAddWithoutValidation("origin", "https://yourmother.com");
        request.Headers.TryAddWithoutValidation("referer", "https://yourmother.com/");
        request.Headers.TryAddWithoutValidation("sec-ch-ua", "^^");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "^^");
        request.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        request.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        request.Headers.TryAddWithoutValidation("sec-fetch-site", "cross-site");
        request.Headers.TryAddWithoutValidation("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/109.0.0.0 Safari/537.36 Edg/109.0.1518.61");

        request.Content = new StringContent(data);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded");
    }

    private async Task GetCode()
    {
        var phrase = confPhrase;
        HttpResponseMessage response = null;
        HttpStatusCode responseCode = HttpStatusCode.Created;
        var retry = false;

        if (!string.IsNullOrWhiteSpace(confCaptchPageData))
        {
            HttpClientHandler handler = CreateHandler();

            using (var httpClient = new HttpClient(handler, true))
            {
                using (var request = new HttpRequestMessage(new HttpMethod("POST"), confURL + "/formResponse"))
                {
                    PrepareRequest(confCaptchPageData, request);
                    try
                    {
                        response = await httpClient.SendAsync(request);
                        responseCode = response.StatusCode;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Bad Proxy: GetCode " + ex.Message.ToString());
                        retry = true;
                    }
                }
            }

            if (retry ||
                responseCode == HttpStatusCode.TooManyRequests ||
                responseCode == HttpStatusCode.Unauthorized ||
                responseCode == HttpStatusCode.Forbidden ||
                responseCode == HttpStatusCode.Gone)
            {
                await GetNewProxy();
                await GetCode();
            }

            if (responseCode == HttpStatusCode.BadRequest)
            {
                var responseHTML = await response.Content.ReadAsStringAsync();
                if (responseHTML.Contains("data-validation-failed=\"true\"") && !File.Exists(badRequest))
                {
                    await File.WriteAllTextAsync(badRequest, "GetCode Failed");
                    throw new Exception("Bad Request");
                }
            }

            if (responseCode == HttpStatusCode.OK)
            {
                var responseHTML = await response.Content.ReadAsStringAsync();
                if (responseHTML.Contains("closedform"))
                {
                    File.Create(formClosed);
                    throw new Exception("Form Closed");
                }
                if (!responseHTML.Contains("Google Docs"))
                {
                    Console.WriteLine("Bad Proxy: GetCode");
                    await GetNewProxy();
                    await GetCode();
                }
            }
        }
        else
        {
            using (var client = new HttpClient())
            {
                response = await client.GetAsync(confURL + "/viewform");
                responseCode = response.StatusCode;
            }

            if (responseCode == HttpStatusCode.OK)
            {
                var responseHTML = await response.Content.ReadAsStringAsync();
                if (responseHTML.Contains("closedform"))
                {
                    File.Create(formClosed);
                    throw new Exception("Form Closed");
                }
            }
        }

        var stringResponse = await response.Content.ReadAsStringAsync();
        var indexOfPhrase = stringResponse.IndexOf(phrase);
        var newResult = stringResponse.Substring(0, indexOfPhrase);
        for (int i = newResult.Length - 1; i >= 0; i--)
        {
            if (newResult[i] == '[')
            {
                newResult = newResult.Substring(i);
                break;
            }
        }
        newResult = newResult.Replace("&quot;", "");
        newResult = newResult.Replace("[", "");
        newResult = newResult.Replace("]", "");
        newResult = newResult.Replace(",", "");
        code = newResult;
    }
}
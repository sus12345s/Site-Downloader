using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

class Program
{
    static readonly HttpClient client = new();
    static readonly HashSet<string> downloadedUrls = new(StringComparer.OrdinalIgnoreCase);
    static string baseFolder = "";
    static SemaphoreSlim semaphore = new(5); // max 5 simultaneous downloads

    static async Task Main()
    {
        PrintBanner();

        Console.Write("Enter URL (e.g. https://example.com): ");
        string url = Console.ReadLine()?.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri baseUri))
        {
            Console.WriteLine("! Invalid URL.");
            return;
        }

        baseFolder = GetAvailableFolderName();
        Directory.CreateDirectory(baseFolder);
        Console.WriteLine($"> Saving files to folder '{baseFolder}'");

        string html = await DownloadFileAsync(url, "index.html");
        if (html == null)
        {
            Console.WriteLine("! Error downloading main page.");
            return;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var resourceUrls = ExtractResources(doc, baseUri);

        var tasks = new List<Task>();
        foreach (var resourceUrl in resourceUrls)
        {
            if (!string.IsNullOrWhiteSpace(resourceUrl))
                tasks.Add(ProcessResourceAsync(resourceUrl, baseUri));
        }
        await Task.WhenAll(tasks);

        Console.WriteLine($"> Done! All files saved in folder '{baseFolder}'.");
    }

    static void PrintBanner()
    {
        Console.WriteLine(@"
   ▄▄▄▄▄▄▄ ▄▄▄ ▄▄▄▄▄▄  ▄▄▄▄▄▄▄      ▄▄▄▄▄▄▄ ▄▄▄▄▄▄   ▄▄▄▄▄▄▄ ▄▄▄▄▄▄▄ ▄▄▄   ▄ 
   █       █   █      ██       █    █       █   ▄  █ █       █       █   █ █ █
   █  ▄▄▄▄▄█   █  ▄    █    ▄▄▄█    █       █  █ █ █ █   ▄   █       █   █▄█ █
   █ █▄▄▄▄▄█   █ █ █   █   █▄▄▄     █     ▄▄█   █▄▄█▄█  █▄█  █     ▄▄█      ▄█
   █▄▄▄▄▄  █   █ █▄█   █    ▄▄▄█▄▄▄ █    █  █    ▄▄  █       █    █  █     █▄ 
    ▄▄▄▄▄█ █   █       █   █▄▄▄█   ██    █▄▄█   █  █ █   ▄   █    █▄▄█    ▄  █
   █▄▄▄▄▄▄▄█▄▄▄█▄▄▄▄▄▄██▄▄▄▄▄▄▄█▄▄▄██▄▄▄▄▄▄▄█▄▄▄█  █▄█▄▄█ █▄▄█▄▄▄▄▄▄▄█▄▄▄█ █▄█
");
    }

    static string GetAvailableFolderName()
    {
        string baseName = "site";
        if (!Directory.Exists(baseName))
            return baseName;

        for (int i = 2; i < 1000; i++)
        {
            string folderName = baseName + i;
            if (!Directory.Exists(folderName))
                return folderName;
        }

        throw new Exception("Too many existing folders: site, site2, ..., site999");
    }

    static HashSet<string> ExtractResources(HtmlDocument doc, Uri baseUri)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // CSS - <link rel="stylesheet" href="...">
        var cssLinks = doc.DocumentNode.SelectNodes("//link[@rel='stylesheet' and @href]");
        if (cssLinks != null)
        {
            foreach (var node in cssLinks)
            {
                string href = node.GetAttributeValue("href", "");
                string fullUrl = GetAbsoluteUrl(baseUri, href);
                if (!string.IsNullOrEmpty(fullUrl))
                    urls.Add(fullUrl);
            }
        }

        // JS - <script src="...">
        var scripts = doc.DocumentNode.SelectNodes("//script[@src]");
        if (scripts != null)
        {
            foreach (var node in scripts)
            {
                string src = node.GetAttributeValue("src", "");
                string fullUrl = GetAbsoluteUrl(baseUri, src);
                if (!string.IsNullOrEmpty(fullUrl))
                    urls.Add(fullUrl);
            }
        }

        // Images, fonts, videos, audios etc. - src or href
        var srcHrefNodes = doc.DocumentNode.SelectNodes("//*[@src or @href]");
        if (srcHrefNodes != null)
        {
            foreach (var node in srcHrefNodes)
            {
                string link = node.GetAttributeValue("src", null) ?? node.GetAttributeValue("href", null);
                if (string.IsNullOrEmpty(link) || link.StartsWith("#") || link.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    continue;

                string fullUrl = GetAbsoluteUrl(baseUri, link);
                if (!string.IsNullOrEmpty(fullUrl))
                    urls.Add(fullUrl);
            }
        }

        return urls;
    }

    static async Task ProcessResourceAsync(string url, Uri baseUri)
    {
        if (downloadedUrls.Contains(url))
            return;

        await semaphore.WaitAsync();
        try
        {
            if (downloadedUrls.Contains(url))
                return;

            string localPath = GetLocalPath(baseUri, url);

            string content = await DownloadFileAsync(url, localPath);
            downloadedUrls.Add(url);

            if (content != null && localPath.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            {
                await ProcessCssContentAsync(content, baseUri);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"! Error downloading {url}: {ex.Message}");
        }
        finally
        {
            semaphore.Release();
        }
    }

    static async Task ProcessCssContentAsync(string cssContent, Uri baseUri)
    {
        var tasks = new List<Task>();

        foreach (Match match in Regex.Matches(cssContent, @"url\(['""]?(.*?)['""]?\)", RegexOptions.IgnoreCase))
        {
            string url = match.Groups[1].Value;
            if (!string.IsNullOrEmpty(url) && !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                string fullUrl = GetAbsoluteUrl(baseUri, url);
                if (!string.IsNullOrEmpty(fullUrl))
                    tasks.Add(ProcessResourceAsync(fullUrl, baseUri));
            }
        }

        foreach (Match match in Regex.Matches(cssContent, @"@import\s+['""]?(.*?)['""]?;", RegexOptions.IgnoreCase))
        {
            string url = match.Groups[1].Value;
            if (!string.IsNullOrEmpty(url))
            {
                string fullUrl = GetAbsoluteUrl(baseUri, url);
                if (!string.IsNullOrEmpty(fullUrl))
                    tasks.Add(ProcessResourceAsync(fullUrl, baseUri));
            }
        }

        await Task.WhenAll(tasks);
    }

    static string GetAbsoluteUrl(Uri baseUri, string relativeOrAbsoluteUrl)
    {
        try
        {
            Uri resultUri = new(relativeOrAbsoluteUrl, UriKind.RelativeOrAbsolute);
            if (!resultUri.IsAbsoluteUri)
                resultUri = new Uri(baseUri, relativeOrAbsoluteUrl);
            return resultUri.ToString();
        }
        catch
        {
            return null;
        }
    }

    static string GetLocalPath(Uri baseUri, string fullUrl)
    {
        try
        {
            Uri uri = new(fullUrl);
            string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

            if (string.IsNullOrEmpty(path) || path.EndsWith(Path.DirectorySeparatorChar.ToString()))
                path += "index.html";

            // Remove illegal filename chars
            path = Regex.Replace(path, @"[\?\*\|<>:""]", "_");

            return path;
        }
        catch
        {
            return "others/file.dat";
        }
    }

    static async Task<string> DownloadFileAsync(string url, string relativePath)
    {
        if (string.IsNullOrEmpty(url))
            return null;

        try
        {
            Console.WriteLine($"> Downloading: {url}");
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"! HTTP error {response.StatusCode} for {url}");
                return null;
            }

            byte[] data = await response.Content.ReadAsByteArrayAsync();

            string fullPath = Path.Combine(baseFolder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? baseFolder);
            await File.WriteAllBytesAsync(fullPath, data);

            // If text (html, css, js), return UTF8 string
            if (relativePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                relativePath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
                relativePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
                relativePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                return System.Text.Encoding.UTF8.GetString(data);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"! Error downloading {url}: {ex.Message}");
        }

        return null;
    }
}

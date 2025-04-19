/*
 * Copyright (c) .NET Foundation and Contributors
 *
 * This software may be modified and distributed under the terms
 * of the MIT license. See the LICENSE file for details.
 *
 * https://github.com/piranhacms/piranha.core
 *
 */

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Piranha.AspNetCore.Services;
using X.Web.Sitemap;
using X.Web.Sitemap.Extensions;

namespace Piranha.AspNetCore.Http;

/// <summary>
/// Middleware used to ouput a xml sitemap based on
/// the content of the current site.
/// </summary>
public class SitemapMiddleware : MiddlewareBase
{
    private readonly RoutingOptions _options;
    private readonly IMemoryCache _cache;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Creates a new middleware instance.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline</param>
    /// <param name="cache"></param>
    /// <param name="options">The current routing options</param>
    /// <param name="factory">The optional logger factory</param>
    /// <param name="serviceProvider"></param>
    public SitemapMiddleware(RequestDelegate next, IMemoryCache cache, IOptions<RoutingOptions> options, ILoggerFactory factory = null, IServiceProvider serviceProvider = null) : base(next, factory)
    {
        _options = options.Value;
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>
    /// Invokes the middleware.
    /// </summary>
    /// <param name="context">The current http context</param>
    /// <param name="api">The current api</param>
    /// <param name="service">The application service</param>
    /// <returns>An async task</returns>
    public override async Task Invoke(HttpContext context, IApi api, IApplicationService service)
    {
        if (_options.UseSitemapRouting && !IsHandled(context) && !context.Request.Path.Value.StartsWith("/manager/assets/"))
        {
            var url = context.Request.Path.HasValue ? context.Request.Path.Value : "";
            var host = context.Request.Host.Host;
            var scheme = context.Request.Scheme;
            var port = context.Request.Host.Port;
            var prefix = service.Site.SitePrefix != null ?
                $"/{service.Site.SitePrefix}" : "";
            var baseUrl = scheme + "://" + host + (port.HasValue ? $":{port}" : "") + prefix;

            if (url.ToLower() == "/sitemap.xml")
            {
                _logger?.LogInformation($"Sitemap.xml requested, generating");

                // Get the requested site by hostname
                var siteId = service.Site.Id;

                // Attempt to retrieve sitemap from cache
                var cacheKey = $"sitemap_{siteId}";
                if (_cache.TryGetValue(cacheKey, out string cachedSitemap))
                {
                    _logger?.LogInformation($"Cache hit for sitemap with key: {cacheKey}");

                    // Schedule background refresh
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (var scope = _serviceProvider.CreateScope())
                            {
                                var scopedApi = scope.ServiceProvider.GetRequiredService<IApi>();
                                var pages = await scopedApi.Sites.GetSitemapAsync(siteId);

                                if (App.Hooks.OnGenerateSitemap != null)
                                {
                                    pages = App.Hooks.OnGenerateSitemap(Utils.DeepClone(pages));
                                }

                                var sitemap = new Sitemap();
                                foreach (var page in pages)
                                {
                                    if (!page.MetaIndex)
                                        continue;

                                    var urls = await GetPageUrlsAsync(scopedApi, page, baseUrl).ConfigureAwait(false);
                                    if (urls.Count() > 0)
                                    {
                                        sitemap.AddRange(urls);
                                    }
                                }

                                var sitemapXml = sitemap.ToXml();
                                var cacheEntryOptions = new MemoryCacheEntryOptions()
                                    .SetSlidingExpiration(TimeSpan.FromHours(48)); // Adjust cache duration as needed
                                _cache.Set(cacheKey, sitemapXml, cacheEntryOptions);

                                _logger?.LogInformation($"Cache refreshed successfully for key: {cacheKey}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex, $"Error refreshing cache for key: {cacheKey}");
                        }
                    });

                    context.Response.ContentType = "application/xml";
                    await context.Response.WriteAsync(cachedSitemap);
                    return;
                }

                _logger?.LogInformation($"Cache miss for sitemap with key: {cacheKey}. Generating sitemap.");

                // Generate sitemap and cache it
                var pages = await api.Sites.GetSitemapAsync(siteId);

                if (App.Hooks.OnGenerateSitemap != null)
                {
                    pages = App.Hooks.OnGenerateSitemap(Utils.DeepClone(pages));
                }

                var sitemap = new Sitemap();
                foreach (var page in pages)
                {
                    if (!page.MetaIndex)
                        continue;

                    var urls = await GetPageUrlsAsync(api, page, baseUrl).ConfigureAwait(false);
                    if (urls.Count > 0)
                    {
                        sitemap.AddRange(urls);
                    }
                }

                var sitemapXml = sitemap.ToXml();
                var cacheEntryOptions = new MemoryCacheEntryOptions()
                    .SetSlidingExpiration(TimeSpan.FromHours(48)); // Adjust cache duration as needed
                _cache.Set(cacheKey, sitemapXml, cacheEntryOptions);

                _logger?.LogInformation($"Sitemap cached successfully with key: {cacheKey}");

                context.Response.ContentType = "application/xml";
                await context.Response.WriteAsync(sitemapXml);
                return;
            }
        }
        await _next.Invoke(context);
    }

    private async Task<List<Url>> GetPageUrlsAsync(IApi api, Piranha.Models.SitemapItem item, string baseUrl)
    {
        var urls = new List<Url>();

        if (item.MetaIndex && item.Published.HasValue && item.Published.Value <= DateTime.Now)
        {
            urls.Add(new Url
            {
                ChangeFrequency = ChangeFrequency.Daily,
                // If the Permalink contains an absolute Uri (e.g. redirection), don't prefix with the baseUrl
                Location = Uri.IsWellFormedUriString(item.Permalink, UriKind.Absolute) ? item.Permalink : baseUrl + item.Permalink,
                Priority = item.MetaPriority,
                TimeStamp = item.LastModified
            });

            // Get all posts for the blog
            var posts = await api.Posts.GetAllAsync(item.Id);
            foreach (var post in posts)
            {
                if (post.MetaIndex && post.Published.HasValue && post.Published.Value <= DateTime.Now)
                {
                    urls.Add(new Url
                    {
                        ChangeFrequency = ChangeFrequency.Daily,
                        // If the Permalink contains an absolute Uri (e.g. redirection), don't prefix with the baseUrl
                        Location = Uri.IsWellFormedUriString(post.Permalink, UriKind.Absolute) ? item.Permalink : baseUrl + post.Permalink,
                        Priority = post.MetaPriority,
                        TimeStamp = post.LastModified
                    });
                }
            }

            foreach (var child in item.Items)
            {
                var childUrls = await GetPageUrlsAsync(api, child, baseUrl).ConfigureAwait(false);

                if (childUrls.Count > 0)
                {
                    urls.AddRange(childUrls);
                }
            }
        }
        return urls;
    }
}

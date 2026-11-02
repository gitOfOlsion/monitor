# Getting monitorhubs.net into search engines

The site already serves everything crawlers need:

| What | Where |
|---|---|
| Server-rendered HTML for `/` and `/demo` | Blazor prerendering |
| Title, description, canonical, Open Graph, Twitter card | `Web/Components/Shared/Seo.razor` |
| Structured data (Organization, WebSite, SoftwareApplication, FAQPage) | `Web/Components/Pages/Index.razor` |
| `robots.txt` and `sitemap.xml` | `Web/Program.cs` (built from `PUBLIC_URL`) |
| `noindex` on sign-in, signed-in pages, 404 and API paths | `MainLayout.razor`, `Login.razor`, `Caddy/Caddyfile` |
| Share image 1200×630 and app icons | `Web/wwwroot/og-image.png`, regenerate with `Web/Branding/render.sh` |
| IndexNow key | `Web/wwwroot/61abc7ba34ebf2d90cd8cd82bfd05259.txt` |

The steps below need your own accounts, so they can't be automated from the code.

## 1. Google Search Console (Google)
1. Go to https://search.google.com/search-console and add a **Domain** property for `monitorhubs.net`.
   Verify with the DNS TXT record it shows you (recommended), **or** choose "HTML tag", copy only the
   `content="…"` value and set it in `.env`:
   ```
   GOOGLE_SITE_VERIFICATION=the-code
   ```
   then `docker compose up -d web`.
2. **Sitemaps** → submit `https://monitorhubs.net/sitemap.xml`.
3. **URL inspection** → enter `https://monitorhubs.net/` → **Request indexing**. Repeat for `/demo`.

## 2. Bing Webmaster Tools (Bing, DuckDuckGo, Yahoo, Ecosia, ChatGPT search)
1. Go to https://www.bing.com/webmasters and choose **Import from Google Search Console** (fastest),
   or add the site and set `BING_SITE_VERIFICATION=the-code` in `.env`.
2. Submit `https://monitorhubs.net/sitemap.xml`.

## 3. Yandex Webmaster (optional, for Russian-speaking markets)
https://webmaster.yandex.com → add site → set `YANDEX_VERIFICATION=the-code` → submit the sitemap.

## 4. IndexNow: instant notice after each deploy
Bing, Yandex, Seznam and Naver accept IndexNow pings. After deploying a content change, run:

```bash
curl -s -H "Content-Type: application/json" https://api.indexnow.org/indexnow -d '{"host":"monitorhubs.net","key":"61abc7ba34ebf2d90cd8cd82bfd05259","keyLocation":"https://monitorhubs.net/61abc7ba34ebf2d90cd8cd82bfd05259.txt","urlList":["https://monitorhubs.net/","https://monitorhubs.net/demo"]}'
```

A `200` or `202` response means it was accepted.

## 5. Check it
- Rich results: https://search.google.com/test/rich-results?url=https://monitorhubs.net/
- Share previews: https://www.opengraph.xyz/url/https%3A%2F%2Fmonitorhubs.net
- Speed and Core Web Vitals: https://pagespeed.web.dev/?url=https://monitorhubs.net/

## 6. What moves rankings from here
Technical SEO is now in place; ranking depends on content and links.
- **Links from other sites.** List the product on AlternativeTo (as an AnyDesk / RustDesk alternative),
  Product Hunt, G2, Capterra and SourceForge, and mention it on relevant forums (r/sysadmin, r/k12sysadmin).
- **One page per search intent.** For example "USB monitoring software for schools",
  "Remote desktop in the browser without a client", "AnyDesk alternative for institutions".
  Each page needs its own `<Seo>` tag and an entry in the sitemap in `Web/Program.cs`.
- **Other languages.** If you target Albanian-speaking users, add translated pages with `hreflang` links.

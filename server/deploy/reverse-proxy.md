# Reverse proxy snippets

The Lodge listens on `127.0.0.1:5280` and needs: WebSocket upgrade on `/lodge/ws`, uploads up to
`LODGE_MAX_FILE_MB` (+ a little), and long-lived connections (voice/chat stay open for hours).
Pick the one matching your web server and add it to the **existing HTTPS site** (your website keeps working).

## nginx (inside the existing `server { listen 443 ssl; ... }` block)

```nginx
location /lodge/ {
    proxy_pass http://127.0.0.1:5280;      # no trailing slash: keeps the /lodge prefix
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 1h;
    proxy_send_timeout 1h;
    proxy_buffering off;
    proxy_request_buffering off;
    client_max_body_size 30m;
}
```
Then `sudo nginx -t && sudo systemctl reload nginx`.

## Caddy (inside the existing site block)

```caddy
handle /lodge/* {
    reverse_proxy 127.0.0.1:5280
}
```
Then `sudo systemctl reload caddy`. (Caddy handles WebSockets and has no upload limit by default.)

## Apache (inside the existing `<VirtualHost *:443>`)

```apache
# sudo a2enmod proxy proxy_http proxy_wstunnel
ProxyPreserveHost On
ProxyTimeout 3600
ProxyPass        /lodge/ws  ws://127.0.0.1:5280/lodge/ws
ProxyPass        /lodge/    http://127.0.0.1:5280/lodge/
ProxyPassReverse /lodge/    http://127.0.0.1:5280/lodge/
<Location /lodge/>
    LimitRequestBody 31457280
</Location>
```
Then `sudo apachectl configtest && sudo systemctl reload apache2`.

## Subdomain instead of /lodge

Set `LODGE_PATHBASE=` (empty) in `/etc/lodge/lodge.env`, `sudo systemctl restart lodge`, and proxy the whole
subdomain (`location / { ... }`) to `127.0.0.1:5280` with the same settings.

## Proxy running in Docker (Nginx Proxy Manager, Traefik, ...)

A container can't reach `127.0.0.1` on the host. Set `LODGE_URLS=http://172.17.0.1:5280` (the docker0 bridge IP,
check with `ip addr show docker0`), restart lodge, point the proxy at `172.17.0.1:5280`, enable WebSocket support
for that route, and make sure port 5280 is NOT opened in the host firewall to the internet.

## Check

```bash
curl https://yoursite/lodge/health                    # {"ok":true,...}
curl -i -H "Connection: Upgrade" -H "Upgrade: websocket" -H "Sec-WebSocket-Version: 13" \
     -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" "https://yoursite/lodge/ws?code=WRONG"   # expect 401
```

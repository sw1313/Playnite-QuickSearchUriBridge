# Playnite-QuickSearchUriBridge
Playnite QuickSearch plugin, which can pass uri parameters from the browser to the QuickSearch global search box.
Transmission format<br>
query parameters<br>
playnite://quicksearch?q=<URL encoded keywords><br>
Path segment<br>
playnite://quicksearch/q/<URL encoded keywords><br>
Base64 (UTF-8) path segment qb64<br>
playnite://quicksearch/qb64/<Base64(UTF-8)><br>
Multi-segment path automatically inserts spaces<br>
playnite://quicksearch/<seg1>/<seg2>/<br>

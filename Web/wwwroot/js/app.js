// Peripheral Monitor — client-side helpers invoked from Blazor via IJSRuntime

/**
 * Trigger a browser download of a base64-encoded file.
 * @param {string} fileName - suggested file name for the download
 * @param {string} mimeType - MIME type of the content
 * @param {string} base64Content - base64-encoded file bytes
 */
function pmDownloadFile(fileName, mimeType, base64Content) {
    const byteChars = atob(base64Content);
    const byteNumbers = new Array(byteChars.length);
    for (let i = 0; i < byteChars.length; i++) {
        byteNumbers[i] = byteChars.charCodeAt(i);
    }
    const byteArray = new Uint8Array(byteNumbers);
    const blob = new Blob([byteArray], { type: mimeType || 'application/octet-stream' });

    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
}

/**
 * Open a custom-protocol link (e.g. rustdesk://connect/...) to hand off to a
 * locally-installed application. Uses a hidden iframe so the browser doesn't
 * navigate away or show "blocked popup" for the current tab.
 * @param {string} uri - the custom-protocol URI to open
 */
function pmOpenExternalLink(uri) {
    const iframe = document.createElement('iframe');
    iframe.style.display = 'none';
    iframe.src = uri;
    document.body.appendChild(iframe);
    setTimeout(() => document.body.removeChild(iframe), 1000);
}

/**
 * Return the browser's UTC offset in minutes (e.g. +120 for CEST).
 * Called once on page load to let the server format times in the user's timezone.
 */
function pmGetTimezoneOffsetMinutes() {
    return -new Date().getTimezoneOffset(); // JS returns opposite sign convention
}

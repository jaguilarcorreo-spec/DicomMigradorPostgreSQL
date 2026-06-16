// Download helper for Discovery Engine exports
window.downloadFile = (filename, contentBase64, mimeType) => {
    const link = document.createElement('a');
    link.href = `data:${mimeType};base64,${contentBase64}`;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};

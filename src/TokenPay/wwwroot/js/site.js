(function () {
    'use strict'

    var clipboard = new ClipboardJS('.btn');

    clipboard.on('success', function (e) {
        alert('Copy successful! \nCopied content:' + e.text)
    });

    clipboard.on('error', function (e) {
    });

    console.log("Powered by TokenPay");
    console.log("https://github.com/LightCountry/TokenPay");
})()
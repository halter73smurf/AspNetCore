﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Microsoft.AspNetCore.ResponseCompression.Tests
{
    public class ResponseCompressionMiddlewareTest
    {
        private const string TextPlain = "text/plain";

        [Fact]
        public void Options_HttpsDisabledByDefault()
        {
            var options = new ResponseCompressionOptions();

            Assert.False(options.EnableHttps);
        }

        [Fact]
        public async Task Request_NoAcceptEncoding_Uncompressed()
        {
            var response = await InvokeMiddleware(100, requestAcceptEncodings: null, responseType: TextPlain);

            CheckResponseNotCompressed(response, expectedBodyLength: 100);
        }

        [Fact]
        public async Task Request_AcceptGzipDeflate_ComrpessedGzip()
        {
            var response = await InvokeMiddleware(100, requestAcceptEncodings: new string[] { "gzip", "deflate" }, responseType: TextPlain);

            CheckResponseCompressed(response, expectedBodyLength: 24);
        }

        [Fact]
        public async Task Request_AcceptUnknown_NotCompressed()
        {
            var response = await InvokeMiddleware(100, requestAcceptEncodings: new string[] { "unknown" }, responseType: TextPlain);

            CheckResponseNotCompressed(response, expectedBodyLength: 100);
        }

        [Fact]
        public void NoMimeTypes_Throws()
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddResponseCompression();
                })
                .Configure(app =>
                {
                    app.UseResponseCompression();
                    app.Run(context =>
                    {
                        context.Response.ContentType = TextPlain;
                        return context.Response.WriteAsync(new string('a', 100));
                    });
                });

            Assert.Throws<InvalidOperationException>(() => new TestServer(builder));
        }

        [Theory]
        [InlineData("text/plain")]
        [InlineData("text/PLAIN")]
        [InlineData("text/plain; charset=ISO-8859-4")]
        [InlineData("text/plain ; charset=ISO-8859-4")]
        public async Task ContentType_WithCharset_Compress(string contentType)
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddResponseCompression(TextPlain);
                })
                .Configure(app =>
                {
                    app.UseResponseCompression();
                    app.Run(context =>
                    {
                        context.Response.ContentType = contentType;
                        return context.Response.WriteAsync(new string('a', 100));
                    });
                });

            var server = new TestServer(builder);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "");
            request.Headers.AcceptEncoding.ParseAdd("gzip");

            var response = await client.SendAsync(request);

            Assert.Equal(24, response.Content.ReadAsByteArrayAsync().Result.Length);
        }

        [Theory]
        [InlineData("")]
        [InlineData("text/plain2")]
        public async Task MimeTypes_OtherContentTypes_NoMatch(string contentType)
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddResponseCompression(TextPlain);
                })
                .Configure(app =>
                {
                    app.UseResponseCompression();
                    app.Run(context =>
                    {
                        context.Response.ContentType = contentType;
                        return context.Response.WriteAsync(new string('a', 100));
                    });
                });

            var server = new TestServer(builder);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "");
            request.Headers.AcceptEncoding.ParseAdd("gzip");

            var response = await client.SendAsync(request);

            Assert.Equal(100, response.Content.ReadAsByteArrayAsync().Result.Length);
        }

        [Fact]
        public async Task Request_AcceptStar_Compressed()
        {
            var response = await InvokeMiddleware(100, requestAcceptEncodings: new string[] { "*" }, responseType: TextPlain);

            CheckResponseCompressed(response, expectedBodyLength: 24);
        }

        [Fact]
        public async Task Request_AcceptIdentity_NotCompressed()
        {
            var response = await InvokeMiddleware(100, requestAcceptEncodings: new string[] { "identity" }, responseType: TextPlain);

            CheckResponseNotCompressed(response, expectedBodyLength: 100);
        }

        [Theory]
        [InlineData(new string[] { "identity;q=0.5", "gzip;q=1" }, 24)]
        [InlineData(new string[] { "identity;q=0", "gzip;q=0.8" }, 24)]
        [InlineData(new string[] { "identity;q=0.5", "gzip" }, 24)]
        public async Task Request_AcceptWithHigherCompressionQuality_Compressed(string[] acceptEncodings, int expectedBodyLength)
        {
            var response = await InvokeMiddleware(100, requestAcceptEncodings: acceptEncodings, responseType: TextPlain);

            CheckResponseCompressed(response, expectedBodyLength: expectedBodyLength);
        }

        [Theory]
        [InlineData(new string[] { "gzip;q=0.5", "identity;q=0.8" }, 100)]
        public async Task Request_AcceptWithhigherIdentityQuality_NotCompressed(string[] acceptEncodings, int expectedBodyLength)
        {
            var response = await InvokeMiddleware(100, requestAcceptEncodings: acceptEncodings, responseType: TextPlain);

            CheckResponseNotCompressed(response, expectedBodyLength: expectedBodyLength);
        }

        [Fact]
        public async Task Response_UnauthorizedMimeType_NotCompressed()
        {
            var response = await InvokeMiddleware(100, requestAcceptEncodings: new string[] { "gzip" }, responseType: "text/html");

            CheckResponseNotCompressed(response, expectedBodyLength: 100);
        }

        [Fact]
        public async Task Response_WithContentRange_NotCompressed()
        {
            var response = await InvokeMiddleware(50, requestAcceptEncodings: new string[] { "gzip" }, responseType: TextPlain, addResponseAction: (r) =>
            {
                r.Headers[HeaderNames.ContentRange] = "1-2/*";
            });

            CheckResponseNotCompressed(response, expectedBodyLength: 50);
        }

        [Fact]
        public async Task Response_WithContentEncodingAlreadySet_Stacked()
        {
            var otherContentEncoding = "something";

            var response = await InvokeMiddleware(50, requestAcceptEncodings: new string[] { "gzip" }, responseType: TextPlain, addResponseAction: (r) =>
            {
                r.Headers[HeaderNames.ContentEncoding] = otherContentEncoding;
            });

            Assert.True(response.Content.Headers.ContentEncoding.Contains(otherContentEncoding));
            Assert.True(response.Content.Headers.ContentEncoding.Contains("gzip"));
            Assert.Equal(24, response.Content.Headers.ContentLength);
        }

        [Theory]
        [InlineData(false, 100)]
        [InlineData(true, 24)]
        public async Task Request_Https_CompressedIfEnabled(bool enableHttps, int expectedLength)
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddResponseCompression(options =>
                    {
                        options.EnableHttps = enableHttps;
                        options.MimeTypes = new[] { TextPlain };
                    });
                })
                .Configure(app =>
                {
                    app.UseResponseCompression();
                    app.Run(context =>
                    {
                        context.Response.ContentType = TextPlain;
                        return context.Response.WriteAsync(new string('a', 100));
                    });
                });

            var server = new TestServer(builder);
            server.BaseAddress = new Uri("https://localhost/");
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "");
            request.Headers.AcceptEncoding.ParseAdd("gzip");

            var response = await client.SendAsync(request);

            Assert.Equal(expectedLength, response.Content.ReadAsByteArrayAsync().Result.Length);
        }

        private Task<HttpResponseMessage> InvokeMiddleware(int uncompressedBodyLength, string[] requestAcceptEncodings, string responseType, Action<HttpResponse> addResponseAction = null)
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddResponseCompression(TextPlain);
                })
                .Configure(app =>
                {
                    app.UseResponseCompression();
                    app.Run(context =>
                    {
                        context.Response.Headers[HeaderNames.ContentMD5] = "MD5";
                        context.Response.ContentType = responseType;
                        if (addResponseAction != null)
                        {
                            addResponseAction(context.Response);
                        }
                        return context.Response.WriteAsync(new string('a', uncompressedBodyLength));
                    });
                });

            var server = new TestServer(builder);
            var client = server.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Get, "");
            for (var i = 0; i < requestAcceptEncodings?.Length; i++)
            {
                request.Headers.AcceptEncoding.Add(System.Net.Http.Headers.StringWithQualityHeaderValue.Parse(requestAcceptEncodings[i]));
            }

            return client.SendAsync(request);
        }

        private void CheckResponseCompressed(HttpResponseMessage response, int expectedBodyLength)
        {
            IEnumerable<string> contentMD5 = null;

            Assert.False(response.Headers.TryGetValues(HeaderNames.ContentMD5, out contentMD5));
            Assert.Single(response.Content.Headers.ContentEncoding, "gzip");
            Assert.Equal(expectedBodyLength, response.Content.Headers.ContentLength);
        }

        private void CheckResponseNotCompressed(HttpResponseMessage response, int expectedBodyLength)
        {
            Assert.NotNull(response.Headers.GetValues(HeaderNames.ContentMD5));
            Assert.Empty(response.Content.Headers.ContentEncoding);
            Assert.Equal(expectedBodyLength, response.Content.Headers.ContentLength);
        }
    }
}

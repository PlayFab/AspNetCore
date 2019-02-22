// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.IntegrationTesting;
using Microsoft.AspNetCore.Server.IntegrationTesting.Common;
using Microsoft.AspNetCore.Testing;
using Microsoft.AspNetCore.Testing.xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Microsoft.AspNetCore.StaticFiles
{
    [SkipOnHelix] // https://github.com/aspnet/AspNetCore/issues/7847
    public class StaticFileMiddlewareTests : LoggedTest
    {
        [ConditionalFact]
        public async Task ReturnsNotFoundWithoutWwwroot()
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(services => services.AddSingleton(LoggerFactory))
                .UseKestrel()
                .Configure(app => app.UseStaticFiles());

            using (var server = builder.Start(TestUrlHelper.GetTestUrl(ServerType.Kestrel)))
            {
                using (var client = new HttpClient { BaseAddress = new Uri(server.GetAddress()) })
                {
                    var response = await client.GetAsync("TestDocument.txt");

                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                }
            }
        }

        [ConditionalFact]
        public async Task FoundFile_LastModifiedTrimsSeconds()
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(services => services.AddSingleton(LoggerFactory))
                .UseKestrel()
                .UseWebRoot(AppContext.BaseDirectory)
                .Configure(app => app.UseStaticFiles());

            using (var server = builder.Start(TestUrlHelper.GetTestUrl(ServerType.Kestrel)))
            {
                using (var client = new HttpClient { BaseAddress = new Uri(server.GetAddress()) })
                {
                    var last = File.GetLastWriteTimeUtc(Path.Combine(AppContext.BaseDirectory, "TestDocument.txt"));
                    var response = await client.GetAsync("TestDocument.txt");

                    var trimmed = new DateTimeOffset(last.Year, last.Month, last.Day, last.Hour, last.Minute, last.Second, TimeSpan.Zero).ToUniversalTime();

                    Assert.Equal(response.Content.Headers.LastModified.Value, trimmed);
                }
            }
        }

        [ConditionalTheory]
        [MemberData(nameof(ExistingFiles))]
        public async Task FoundFile_Served_All(string baseUrl, string baseDir, string requestUrl)
        {
            await FoundFile_Served(baseUrl, baseDir, requestUrl);
        }

        [ConditionalTheory]
        [OSSkipCondition(OperatingSystems.Linux)]
        [OSSkipCondition(OperatingSystems.MacOSX)]
        [InlineData("", @".", "/testDocument.Txt")]
        [InlineData("/somedir", @".", "/somedir/Testdocument.TXT")]
        [InlineData("/SomeDir", @".", "/soMediR/testdocument.txT")]
        [InlineData("/somedir", @"SubFolder", "/somedir/Ranges.tXt")]
        public async Task FoundFile_Served_Windows(string baseUrl, string baseDir, string requestUrl)
        {
            await FoundFile_Served(baseUrl, baseDir, requestUrl);
        }

        private async Task FoundFile_Served(string baseUrl, string baseDir, string requestUrl)
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(services => services.AddSingleton(LoggerFactory))
                .UseKestrel()
                .UseWebRoot(Path.Combine(AppContext.BaseDirectory, baseDir))
                .Configure(app => app.UseStaticFiles(new StaticFileOptions
                {
                    RequestPath = new PathString(baseUrl),
                }));

            using (var server = builder.Start(TestUrlHelper.GetTestUrl(ServerType.Kestrel)))
            {
                var hostingEnvironment = server.Services.GetService<IWebHostEnvironment>();

                using (var client = new HttpClient { BaseAddress = new Uri(server.GetAddress()) })
                {
                    var fileInfo = hostingEnvironment.WebRootFileProvider.GetFileInfo(Path.GetFileName(requestUrl));
                    var response = await client.GetAsync(requestUrl);
                    var responseContent = await response.Content.ReadAsByteArrayAsync();

                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal("text/plain", response.Content.Headers.ContentType.ToString());
                    Assert.True(response.Content.Headers.ContentLength == fileInfo.Length);
                    Assert.Equal(response.Content.Headers.ContentLength, responseContent.Length);

                    using (var stream = fileInfo.CreateReadStream())
                    {
                        var fileContents = new byte[stream.Length];
                        stream.Read(fileContents, 0, (int)stream.Length);
                        Assert.True(responseContent.SequenceEqual(fileContents));
                    }
                }
            }
        }

        [ConditionalTheory]
        [MemberData(nameof(ExistingFiles))]
        public async Task HeadFile_HeadersButNotBodyServed(string baseUrl, string baseDir, string requestUrl)
        {
            var builder = new WebHostBuilder()
                .ConfigureServices(services => services.AddSingleton(LoggerFactory))
                .UseKestrel()
                .UseWebRoot(Path.Combine(AppContext.BaseDirectory, baseDir))
                .Configure(app => app.UseStaticFiles(new StaticFileOptions
                {
                    RequestPath = new PathString(baseUrl),
                }));

            using (var server = builder.Start(TestUrlHelper.GetTestUrl(ServerType.Kestrel)))
            {
                var hostingEnvironment = server.Services.GetService<IWebHostEnvironment>();

                using (var client = new HttpClient { BaseAddress = new Uri(server.GetAddress()) })
                {
                    var fileInfo = hostingEnvironment.WebRootFileProvider.GetFileInfo(Path.GetFileName(requestUrl));
                    var request = new HttpRequestMessage(HttpMethod.Head, requestUrl);
                    var response = await client.SendAsync(request);

                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal("text/plain", response.Content.Headers.ContentType.ToString());
                    Assert.True(response.Content.Headers.ContentLength == fileInfo.Length);
                    Assert.Empty((await response.Content.ReadAsByteArrayAsync()));
                }
            }
        }

        public static IEnumerable<object[]> ExistingFiles => new[]
        {
            new[] {"", @".", "/TestDocument.txt"},
            new[] {"/somedir", @".", "/somedir/TestDocument.txt"},
            new[] {"/SomeDir", @".", "/soMediR/TestDocument.txt"},
            new[] {"", @"SubFolder", "/ranges.txt"},
            new[] {"/somedir", @"SubFolder", "/somedir/ranges.txt"},
            new[] {"", @"SubFolder", "/Empty.txt"}
        };

        [ConditionalFact]
        public Task ClientDisconnect_Kestrel_NoWriteExceptionThrown()
        {
            return ClientDisconnect_NoWriteExceptionThrown(ServerType.Kestrel);
        }

        [ConditionalFact]
        [OSSkipCondition(OperatingSystems.Linux)]
        [OSSkipCondition(OperatingSystems.MacOSX)]
        public Task ClientDisconnect_WebListener_NoWriteExceptionThrown()
        {
            return ClientDisconnect_NoWriteExceptionThrown(ServerType.HttpSys);
        }

        private async Task ClientDisconnect_NoWriteExceptionThrown(ServerType serverType)
        {
            var interval = TimeSpan.FromSeconds(15);
            var requestReceived = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestCancelled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var responseComplete = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception exception = null;
            var builder = new WebHostBuilder()
                .ConfigureServices(services => services.AddSingleton(LoggerFactory))
                .UseWebRoot(Path.Combine(AppContext.BaseDirectory))
                .Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        try
                        {
                            requestReceived.SetResult(0);
                            await requestCancelled.Task.TimeoutAfter(interval);
                            Assert.True(context.RequestAborted.WaitHandle.WaitOne(interval), "not aborted");
                            await next();
                        }
                        catch (Exception ex)
                        {
                            exception = ex;
                        }
                        responseComplete.SetResult(0);
                    });
                    app.UseStaticFiles();
                });

            if (serverType == ServerType.HttpSys)
            {
                builder.UseHttpSys();
            }
            else if (serverType == ServerType.Kestrel)
            {
                builder.UseKestrel();
            }

            using (var server = builder.Start(TestUrlHelper.GetTestUrl(serverType)))
            {
                // We don't use HttpClient here because it's disconnect behavior varies across platforms.
                var socket = SendSocketRequestAsync(server.GetAddress(), "/TestDocument1MB.txt");
                await requestReceived.Task.TimeoutAfter(interval);

                socket.LingerState = new LingerOption(true, 0);
                socket.Dispose();
                requestCancelled.SetResult(0);

                await responseComplete.Task.TimeoutAfter(interval);
                Assert.Null(exception);
            }
        }

        private Socket SendSocketRequestAsync(string address, string path, string method = "GET")
        {
            var uri = new Uri(address);
            var builder = new StringBuilder();
            builder.Append($"{method} {path} HTTP/1.1\r\n");
            builder.Append($"HOST: {uri.Authority}\r\n\r\n");

            byte[] request = Encoding.ASCII.GetBytes(builder.ToString());

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(IPAddress.Loopback, uri.Port);
            socket.Send(request);
            return socket;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Linq;
using System.Text.RegularExpressions;

CreateHostBuilder(args).Build().Run();

static IHostBuilder CreateHostBuilder(string[] args) =>
	Host.CreateDefaultBuilder(args)
		.ConfigureWebHostDefaults(webBuilder =>
		{
			webBuilder
			.ConfigureServices(services =>
			{
				services.AddSpaStaticFiles(configuration =>
				{
					configuration.RootPath = "ClientApp/dist";
				});
			})
			.Configure(app =>
			{
				var env = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
				var config = app.ApplicationServices.GetRequiredService<IConfiguration>();

				if (env.IsDevelopment())
				{
					app.UseDeveloperExceptionPage();
				}

				app.UseHttpsRedirection();

				app.UseStaticFiles();

				if (!env.IsDevelopment())
				{
					app.UseSpaStaticFiles();
				}

				app.UseRouting();

				app.UseEndpoints(endpoints =>
				{
					endpoints.MapPost("/upload", async context =>
					{
						if (!context.Request.HasFormContentType)
						{
							context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
							await context.Response.WriteAsync("Unsupported media type");
							return;
						}

						if (context.Request.Form.Files.Any() && context.Request.Form.Files.Count > 1)
						{
							context.Response.StatusCode = StatusCodes.Status400BadRequest;
							await context.Response.WriteAsync("Only 1 file allowed.");
							return;
						}

						var formFile = context.Request.Form.Files[0];

						if (formFile.Length <= 0)
						{
							context.Response.StatusCode = StatusCodes.Status400BadRequest;
							await context.Response.WriteAsync("Invalid file.");
							return;
						}
						
						await using var uploadFileStream = formFile.OpenReadStream();
						
						var reader = new StreamReader(uploadFileStream);
						var input = reader.ReadToEnd();

						uploadFileStream.Close();

						const string pattern = @"\btvg-name=""----([^""]+)"".*\r?\n";
						var channels = new Dictionary<int, string>();

						foreach (Match m in Regex.Matches(input, pattern))
						{
							var clean = m.Groups[1].Value.Replace("-", string.Empty).Replace(" ", string.Empty);

							var index = LineFromPos(input, m.Groups[1].Index);
							channels.Add(index, $"tvg-name: {clean}, index: {index}");
						}

						static int LineFromPos(string input, int indexPosition)
						{
							var lineNumber = 1;
							for (var i = 0; i < indexPosition; i++)
							{
								if (input[i] == '\n') lineNumber++;
							}
							return lineNumber;
						}

						await context.Response.WriteAsJsonAsync(new
						{
							Message = $"Request submitted successfully {input.Length}",
							Channels = channels.OrderBy(x => x.Key)
						});
					});
				});

				app.UseSpa(spa =>
				{
					spa.Options.SourcePath = "ClientApp";

					if (env.IsDevelopment())
					{
						spa.UseProxyToSpaDevelopmentServer("http://localhost:4200");
					}
				});
			});
		});
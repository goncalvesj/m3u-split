using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace WebApplication2.Controllers
{
	[ApiController]
	[Route("[controller]")]
	public class WeatherForecastController : ControllerBase
	{
		private static readonly string[] Summaries = new[]
		{
			"Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
		};

		private readonly ILogger<WeatherForecastController> _logger;

		public WeatherForecastController(ILogger<WeatherForecastController> logger)
		{
			_logger = logger;
		}

		//[HttpGet]
		//public IEnumerable<WeatherForecast> Get()
		//{
		//	var rng = new Random();
		//	return Enumerable.Range(1, 5).Select(index => new WeatherForecast
		//		{
		//			Date = DateTime.Now.AddDays(index),
		//			TemperatureC = rng.Next(-20, 55),
		//			Summary = Summaries[rng.Next(Summaries.Length)]
		//		})
		//		.ToArray();
		//}

		//[HttpGet]
		//public string Get()
		//{
		//	return "Hello";
		//}

		[HttpGet]
		public Data Get()
		{
			return new() { Message = "Hello" };
		}
	}

	public class Data
	{
		public string Message { get; set; }

	}
	public class WeatherForecast
	{
		public DateTime Date { get; set; }

		public int TemperatureC { get; set; }

		public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);

		public string Summary { get; set; }
	}
}

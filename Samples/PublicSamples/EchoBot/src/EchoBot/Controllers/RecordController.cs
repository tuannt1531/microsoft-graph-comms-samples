// ***********************************************************************
// Assembly         : EchoBot.Controllers
// Author           : JasonTheDeveloper
// Created          : 09-07-2020
//
// Last Modified By : bcage29
// Last Modified On : 02-28-2022
// ***********************************************************************
// <copyright file="JoinCallController.cs" company="Microsoft">
//     Copyright ©  2023
// </copyright>
// <summary></summary>
// ***********************************************************************
using EchoBot.Bot;
using EchoBot.Constants;
using EchoBot.Models;
using EchoBot.Media;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Net;


namespace EchoBot.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class RecordController : ControllerBase
    {
        private readonly ILogger<RecordController> _logger;

        private readonly RedisService _redisService;

        private readonly AppSettings _settings;

        public RecordController(ILogger<RecordController> logger,
            IOptions<AppSettings> settings
        )
        {
            _logger = logger;
            _settings = settings.Value;
            _redisService = new RedisService(_settings.RedisConnection);
        }

        /// <summary>
        /// The setting for start/stop record.
        /// </summary>
        /// <param name="recordSetting">The record setting.</param>
        /// <returns>The <see cref="HttpResponseMessage" />.</returns>
        [HttpPost]
        public IActionResult Record([FromBody] RecordSetting recordSetting)
        {
            try
            {
                _logger.LogInformation($"Setting {recordSetting}");
                var record = _redisService.GetRecord(recordSetting.MeetingId);
                recordSetting.Record = !record.Record;
                _redisService.SaveRecord(recordSetting.MeetingId, recordSetting);
                return Ok();
            }
            catch (Exception e)
            {
                return Problem(detail: e.StackTrace, statusCode: (int)HttpStatusCode.InternalServerError, title: e.Message);
            }
        }
    }
}

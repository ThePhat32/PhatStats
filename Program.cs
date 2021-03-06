﻿using Newtonsoft.Json;
using RestSharp;
using Phat_Stats.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Net;
using OBSWebsocketDotNet;

namespace Phat_Stats
{
    class Program
    {
        private static readonly RestClient client = new RestClient(AppSettings.Get<string>("ApiUri"));
        private static readonly string printFile = $"{Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)}\\print.txt";
        private static readonly string logFile = $"{Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)}\\log.txt";
        private static bool isOn = false;
        private static DateTime standbyTime = DateTime.Now;
        private static OBSWebsocket obs = new OBSWebsocket();
        private static OutputStatus streamStatus;
        private static bool startStream = false;
        private static bool stopStream = false;


        static void Main(string[] args)
        {
            try
            {
                var message = new StringBuilder();

                message.AppendLine();
                message.AppendLine("==================================");
                message.AppendLine("      Welcome to Phat Stats!      ");
                message.AppendLine("==================================");
                message.AppendLine();
                message.AppendLine("Print details are being written to");
                message.AppendLine(printFile);
                message.AppendLine();

                if (AppSettings.Get<bool>("EnableLog"))
                {
                    File.AppendAllText(logFile, message.ToString());
                }
                Console.WriteLine(message.ToString());

                if (AppSettings.Get<bool>("EnableLog"))
                {
                    File.AppendAllLines(logFile, new List<string>() { $"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - Starting up..." });
                }
                Console.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - Starting up...");

                var startTimeSpan = TimeSpan.Zero;
                var periodTimeSpan = TimeSpan.FromSeconds(2);
                //var periodTimeSpan = TimeSpan.FromHours(1);

                var startStreanTimeSpan = TimeSpan.FromSeconds(20);
                var periodStreamTimeSpan = TimeSpan.FromMinutes(1);

                obs.Connected += Obs_Connected;

                obs.Connect($"ws://{AppSettings.Get<string>("IP")}:{AppSettings.Get<string>("Port")}", AppSettings.Get<string>("Password"));

                var timer = new System.Threading.Timer((e) =>
                {
                    GetPrint();
                }, null, startTimeSpan, periodTimeSpan);

                var streamTimer = new System.Threading.Timer((e) =>
                {
                    CheckStream();
                }, null, startStreanTimeSpan, periodStreamTimeSpan);

                if (AppSettings.Get<bool>("EnableLog"))
                {
                    File.AppendAllLines(logFile, new List<string>() { $"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - Up and running" });
                }
                Console.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - Up and running");

                while (true)
                {
                    System.Console.ReadKey();
                }
            }
            catch (AppSettingNotFoundException ex)
            {
                File.AppendAllLines(logFile, new List<string>() { $"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - ERROR: AppSetting with the key \"{ex.Message}\" is missing" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - ERROR: {ex.Message}");
            }
        }

        private static void Obs_Connected(object sender, EventArgs e)
        {
            streamStatus = obs.GetStreamingStatus();
        }

        public static void GetPrint()
        {
            try
            {
                var request = new RestRequest("api/printer", Method.GET);
                request.AddParameter("apikey", AppSettings.Get<string>("ApiKey"));
                var printerResponse = client.Execute(request);

                request = new RestRequest("api/job", Method.GET);
                request.AddParameter("apikey", AppSettings.Get<string>("ApiKey"));
                var jobResponse = client.Execute(request);

                request = new RestRequest("plugin/filamentmanager/selections", Method.GET);
                request.AddParameter("apikey", AppSettings.Get<string>("ApiKey"));
                var filamentResponse = client.Execute(request);

                var message = new StringBuilder();

                var isStreaming = streamStatus.IsStreaming;

                var job = JsonConvert.DeserializeObject<dynamic>(jobResponse.Content);
                if (job["state"] == "Offline")
                {
                    if (isStreaming)
                    {
                        stopStream = true;
                    }

                    message.AppendLine("Offline");
                }
                else
                {
                    var printer = JsonConvert.DeserializeObject<dynamic>(printerResponse.Content);
                    var filament = JsonConvert.DeserializeObject<dynamic>(filamentResponse.Content);

                    if (printer["state"] == null)
                    {
                        if (standbyTime == DateTime.MinValue)
                        {
                            standbyTime = DateTime.Now;

                            if (AppSettings.Get<bool>("EnableLog"))
                            {
                                File.AppendAllLines(logFile, new List<string>() { $"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - Starting Standby Timer" });
                            }
                            Console.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - Starting Standby Timer");
                        }

                        if (DateTime.Now.Subtract(standbyTime).TotalMinutes > 30 && isStreaming)
                        {
                            PhatOBS.StopStream(obs);
                        }

                        message.AppendLine("Offline");
                    }
                    else if (printer["state"]["text"] != "Printing")
                    {
                        if (standbyTime == DateTime.MinValue)
                        {
                            standbyTime = DateTime.Now;
                            if (AppSettings.Get<bool>("EnableLog"))
                            {
                                File.AppendAllLines(logFile, new List<string>() { $"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - Starting Standby Timer" });
                            }
                            Console.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - Starting Standby Timer");
                        }

                        if (DateTime.Now.Subtract(standbyTime).TotalMinutes > 30 && isStreaming)
                        {
                            stopStream = true;
                        }

                        if (DateTime.Now.Subtract(standbyTime).TotalMinutes > 40 && !isStreaming && isOn)
                        {
                            request = new RestRequest("api/plugin/psucontrol", Method.POST);
                            request.AddQueryParameter("apikey", AppSettings.Get<string>("ApiKey"));
                            request.AddJsonBody(new { command = "turnPSUOff" });
                            client.Execute(request);

                            isOn = false;

                            if (AppSettings.Get<bool>("EnableLog"))
                            {
                                File.AppendAllLines(logFile, new List<string>() { $"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - Powering down printer" });
                            }
                            Console.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - Powering down printer");
                        }

                        message.AppendLine(GetTemps(printer, filament));
                        message.AppendLine(string.Empty);
                        message.AppendLine("Standing By");
                    }
                    else
                    {
                        if (!isStreaming)
                        {
                            startStream = true;
                            isOn = true;
                            standbyTime = DateTime.MinValue;
                        }

                        if (standbyTime != DateTime.MinValue)
                        {
                            standbyTime = DateTime.MinValue;

                            if (AppSettings.Get<bool>("EnableLog"))
                            {
                                File.AppendAllLines(logFile, new List<string>() { $"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - Clearing Standby Timer" });
                            }
                            Console.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - Clearing Standby Timer");
                        }

                        message.AppendLine(GetTemps(printer, filament));

                        var printTimeSpan = TimeSpan.FromSeconds(Convert.ToDouble(job["progress"]["printTime"]));
                        var printTimeLeftSpan = TimeSpan.FromSeconds(0);
                        if (!string.IsNullOrWhiteSpace(job["progress"]["printTimeLeft"].ToString()))
                        {
                            printTimeLeftSpan = TimeSpan.FromSeconds(Convert.ToDouble(job["progress"]["printTimeLeft"]));
                        }
                        var printTimeLeftOrigin = job["progress"]["printTimeLeftOrigin"];

                        var printTime = string.Empty;
                        var printTimeLeft = string.Empty;

                        if (printTimeSpan.Days > 0)
                        {
                            printTime += $"{printTimeSpan.Days}d";
                        }
                        if (printTimeSpan.Hours > 0)
                        {
                            printTime += $"{printTimeSpan.Hours}h";
                        }
                        if (printTimeSpan.Minutes > 0)
                        {
                            printTime += $"{printTimeSpan.Minutes}m";
                        }
                        if (string.IsNullOrWhiteSpace(printTime))
                        {
                            printTime = "< 1 Min";
                        }

                        if (printTimeLeftSpan.Days > 0)
                        {
                            printTimeLeft += $"{printTimeLeftSpan.Days}d";
                        }
                        if (printTimeLeftSpan.Hours > 0)
                        {
                            printTimeLeft += $"{printTimeLeftSpan.Hours}h";
                        }
                        if (printTimeLeftSpan.Minutes > 0)
                        {
                            printTimeLeft += $"{printTimeLeftSpan.Minutes}m";
                        }

                        if (string.IsNullOrWhiteSpace(printTimeLeft))
                        {
                            printTimeLeft = "< 1 Min";
                        }

                        message.AppendLine($"Printing for {printTime} with {printTimeLeft} left ({printTimeLeftOrigin})  {Math.Round(Convert.ToDecimal(job["progress"]["completion"]), 2, MidpointRounding.AwayFromZero)}%");
                        message.AppendLine($"File: {job["job"]["file"]["name"]}");

                        var imageName = $"{job["job"]["file"]["name"].ToString().Replace(".gcode", "")}-{DateTime.Now.ToString("yyyyMMdd")}.png";
                        var imageFile = $"{Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)}\\{imageName}";

                        if (!File.Exists(imageFile))
                        {
                            var client = new WebClient();
                            client.DownloadFile($"{AppSettings.Get<string>("ApiUri")}plugin/UltimakerFormatPackage/thumbnail/{ job["job"]["file"]["name"].ToString().Replace("gcode", "png") }?{ DateTime.Now.ToString("yyyyMMdd") }", imageFile);

                            File.Copy(imageFile, $"{Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)}\\model.png", true);
                        }
                    }
                }

                File.WriteAllText($"{Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)}\\print.txt", message.ToString());
            }
            catch (AppSettingNotFoundException ex)
            {
                File.AppendAllLines(logFile, new List<string>() { $"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - ERROR: AppSetting with the key \"{ex.Message}\" is missing" });
            }
            catch (Exception ex)
            {
                if (AppSettings.Get<bool>("EnableLog"))
                {
                    File.AppendAllLines(logFile, new List<string>() { $"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - {ex.Message}" });
                }
                Console.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss")} - {ex.Message}");
            }
        }

        public static void CheckStream()
        {
            streamStatus = obs.GetStreamingStatus();

            if (startStream && !streamStatus.IsStreaming)
            {
                PhatOBS.StartStream(obs);
                startStream = false;
            }

            if (stopStream && streamStatus.IsStreaming)
            {
                PhatOBS.StopStream(obs);
                stopStream = false;
            }

        }

        private static string GetTemps(dynamic printer, dynamic filament)
        {
            var message = new StringBuilder();

            var toolnumber = 0;

            while (printer["temperature"][$"tool{toolnumber}"] != null)
            {
                var filamentText = string.Empty;

                if ((filament["selections"] as JArray).Count > toolnumber)
                {
                    filamentText = $"{filament["selections"][toolnumber]["spool"]["name"]} ({filament["selections"][toolnumber]["spool"]["profile"]["material"]}) ";
                }

                if (printer["temperature"][$"tool{toolnumber}"]["target"] > 0)
                {
                    message.AppendLine($"Tool 1: {filamentText}{printer["temperature"][$"tool{toolnumber}"]["actual"]}/{printer["temperature"][$"tool{toolnumber}"]["target"]}");
                }
                else
                {
                    if (printer["temperature"][$"tool{toolnumber}"]["actual"] > 40)
                    {
                        message.AppendLine($"Tool {toolnumber + 1}: {filamentText}{printer["temperature"][$"tool{toolnumber}"]["actual"]} (Off)");
                    }
                    else
                    {
                        message.AppendLine($"Tool {toolnumber + 1}: {filamentText}Off");
                    }
                }

                toolnumber++;
            }

            if (printer["temperature"]["bed"] != null)
            {
                if (printer["temperature"]["bed"]["target"] > 0)
                {
                    message.AppendLine($"Bed: {printer["temperature"]["bed"]["actual"]}/{printer["temperature"]["bed"]["target"]}");
                }
                else
                {
                    if (printer["temperature"]["bed"]["actual"] > 28)
                    {
                        message.AppendLine($"Bed: {printer["temperature"]["bed"]["actual"]} (Off)");
                    }
                    else
                    {
                        message.AppendLine($"Bed: Off");
                    }
                }
            }

            return message.ToString().TrimEnd('\r', '\n');
        }
    }
}

﻿using playback_reporting.Data;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

namespace playback_reporting
{
    class EventMonitorEntryPoint : IServerEntryPoint
    {
        private readonly ISessionManager _sessionManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IServerConfigurationManager _config;
        private readonly IServerApplicationHost _appHost;
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IJsonSerializer _jsonSerializer;

        private Dictionary<string, PlaybackTracker> playback_trackers = null;
        private IActivityRepository _repository;

        public EventMonitorEntryPoint(ISessionManager sessionManager,
            ILibraryManager libraryManager, 
            IUserManager userManager, 
            IServerConfigurationManager config,
            IServerApplicationHost appHost,
            ILogManager logger,
            IFileSystem fileSystem,
            IJsonSerializer jsonSerializer)
        {
            _logger = logger.GetLogger("PlaybackReporting");
            _sessionManager = sessionManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _config = config;
            _appHost = appHost;
            _fileSystem = fileSystem;
            _jsonSerializer = jsonSerializer;
            playback_trackers = new Dictionary<string, PlaybackTracker>();
        }

        public void Dispose()
        {

        }

        public void Run()
        {

            var repo = new ActivityRepository(_logger, _config.ApplicationPaths, _fileSystem);
            repo.Initialize();
            _repository = repo;

            _sessionManager.PlaybackStart += _sessionManager_PlaybackStart;
            _sessionManager.PlaybackStopped += _sessionManager_PlaybackStop;
            _sessionManager.PlaybackProgress += _sessionManager_PlaybackProgress;
        }

        void _sessionManager_PlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            string key = e.DeviceId + "-" + e.Users[0].Id.ToString("N") + "-" + e.Item.Id.ToString("N");
            if(playback_trackers.ContainsKey(key))
            {
                //_logger.Info("Playback progress tracker found, processing progress : " + key);
                PlaybackTracker tracker = playback_trackers[key];
                tracker.ProcessProgress(e);
            }
            else
            {
                _logger.Info("Playback progress did not have a tracker : " + key);
            }
        }

        void _sessionManager_PlaybackStop(object sender, PlaybackStopEventArgs e)
        {
            string key = e.DeviceId + "-" + e.Users[0].Id.ToString("N") + "-" + e.Item.Id.ToString("N");
            if (playback_trackers.ContainsKey(key))
            {
                _logger.Info("Playback stop tracker found, processing stop : " + key);
                PlaybackTracker tracker = playback_trackers[key];
                tracker.ProcessStop(e);

                // if playback duration was long enough save the action
                if (tracker.TrackedPlaybackInfo.PlaybackDuration > 20)
                {
                    _logger.Info("Staving playback tracking activity in DB");
                    _repository.AddPlaybackAction(tracker.TrackedPlaybackInfo);
                }
                else
                {
                    _logger.Info("Playback duration not long enough, not storing activity in DB");
                }

                // remove the playback tracer from the map as we no longer need it.
                playback_trackers.Remove(key);
            }
            else
            {
                _logger.Info("Playback stop did not have a tracker : " + key);
            }
        }

        void _sessionManager_PlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            if (e.MediaInfo == null)
            {
                return;
            }

            if (e.Item != null && e.Item.IsThemeMedia)
            {
                // Don't report theme song or local trailer playback
                return;
            }

            if (e.Users.Count == 0)
            {
                return;
            }

            string key = e.DeviceId + "-" + e.Users[0].Id.ToString("N") + "-" + e.Item.Id.ToString("N");
            if (playback_trackers.ContainsKey(key))
            {
                _logger.Info("Removing existing tracker : " + key);
                playback_trackers.Remove(key);
            }
            _logger.Info("Adding playback tracker : " + key);
            PlaybackTracker tracker = new PlaybackTracker(key, _logger);
            tracker.ProcessStart(e);
            playback_trackers.Add(key, tracker);

            // start a task to report playback started
            System.Threading.Tasks.Task.Run(() => StartPlaybackTimer(e));

        }

        public async System.Threading.Tasks.Task StartPlaybackTimer(PlaybackProgressEventArgs e)
        {
            _logger.Info("StartPlaybackTimer : Entered");
            await System.Threading.Tasks.Task.Delay(10000);

            try
            {
                var session = _sessionManager.GetSession(e.DeviceId, e.ClientName, "");
                if (session != null)
                {
                    string event_playing_id = e.Item.Id.ToString("N");
                    string event_user_id = e.Users[0].Id.ToString("N");

                    string session_playing_id = "";
                    if (session.NowPlayingItem != null) session_playing_id = session.NowPlayingItem.Id;
                    string session_user_id = "";
                    if (session.UserId != null) session_user_id = ((Guid)session.UserId).ToString("N");

                    string play_method = "na";
                    if (session.PlayState != null && session.PlayState.PlayMethod != null)
                    {
                        play_method = session.PlayState.PlayMethod.Value.ToString();
                    }
                    if (session.PlayState != null && session.PlayState.PlayMethod == MediaBrowser.Model.Session.PlayMethod.Transcode)
                    {
                        if(session.TranscodingInfo !=  null)
                        {
                            string video_codec = "direct";
                            if(session.TranscodingInfo.IsVideoDirect == false)
                            {
                                video_codec = session.TranscodingInfo.VideoCodec;
                            }
                            string audio_codec = "direct";
                            if (session.TranscodingInfo.IsAudioDirect == false)
                            {
                                audio_codec = session.TranscodingInfo.AudioCodec;
                            }
                            play_method += " (v:" + video_codec + " a:" + audio_codec + ")";
                        }
                    }

                    string item_name = GetItemName(e.Item);
                    string item_id = e.Item.Id.ToString("N");
                    string item_type = e.MediaInfo.Type;

                    _logger.Info("StartPlaybackTimer : event_playing_id   = " + event_playing_id);
                    _logger.Info("StartPlaybackTimer : event_user_id      = " + event_user_id);
                    _logger.Info("StartPlaybackTimer : session_playing_id = " + session_playing_id);
                    _logger.Info("StartPlaybackTimer : session_user_id    = " + session_user_id);
                    _logger.Info("StartPlaybackTimer : play_method        = " + play_method);
                    _logger.Info("StartPlaybackTimer : e.ClientName       = " + e.ClientName);
                    _logger.Info("StartPlaybackTimer : e.DeviceName       = " + e.DeviceName);
                    _logger.Info("StartPlaybackTimer : ItemName           = " + item_name);
                    _logger.Info("StartPlaybackTimer : ItemId             = " + item_id);
                    _logger.Info("StartPlaybackTimer : ItemType           = " + item_type);

                    PlaybackInfo play_info = new PlaybackInfo();
                    play_info.Id = Guid.NewGuid().ToString("N");
                    play_info.Date = DateTime.Now;
                    play_info.ClientName = e.ClientName;
                    play_info.DeviceName = e.DeviceName;
                    play_info.PlaybackMethod = play_method;
                    play_info.UserId = event_user_id;
                    play_info.ItemId = item_id;
                    play_info.ItemName = item_name;
                    play_info.ItemType = item_type;

                    // update tracker with playback info
                    string key = e.DeviceId + "-" + e.Users[0].Id.ToString("N") + "-" + e.Item.Id.ToString("N");
                    if (playback_trackers.ContainsKey(key))
                    {
                        _logger.Info("Playback tracker found, adding playback info : " + key);
                        PlaybackTracker tracker = playback_trackers[key];
                        tracker.TrackedPlaybackInfo = play_info;
                    }
                    else
                    {
                        _logger.Info("Playback trackler not found : " + key);
                    }

                }
                else
                {
                    _logger.Info("StartPlaybackTimer : session Not Found");
                }
            }
            catch(Exception exception)
            {
                _logger.Info("StartPlaybackTimer : Error = " + exception.Message + "\n" + exception.StackTrace);
            }
            _logger.Info("StartPlaybackTimer : Exited");
        }

        private string GetItemName(BaseItem item)
        {
            string item_name = "Not Known";

            if (item == null)
            {
                return item_name;
            }

            if (typeof(Episode) == item.GetType())
            {
                Episode epp_item = item as Episode;
                if (epp_item != null)
                {
                    string series_name = "Not Known";
                    if (epp_item.Series != null && string.IsNullOrEmpty(epp_item.Series.Name) == false)
                    {
                        series_name = epp_item.Series.Name;
                    }
                    string season_no = "00";
                    if (epp_item.Season != null && epp_item.Season.IndexNumber != null)
                    {
                        season_no = String.Format("{0:D2}", epp_item.Season.IndexNumber);
                    }
                    string epp_no = "00";
                    if (epp_item.IndexNumber != null)
                    {
                        epp_no = String.Format("{0:D2}", epp_item.IndexNumber);
                    }
                    item_name = epp_item.Series.Name + " - s" + season_no + "e" + epp_no + " - " + epp_item.Name;
                }
            }
            else
            {
                item_name = item.Name;
            }

            return item_name;
        }
    }
}

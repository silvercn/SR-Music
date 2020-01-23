﻿using DCS_SR_Music.SRS_Helpers;
using FragLabs.Audio.Codecs;
using NAudio.Wave;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Application = FragLabs.Audio.Codecs.Opus.Application;

namespace DCS_SR_Music.Network
{
    public class MusicController
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private int stationNumber;
        private string directory;
        private Dictionary<string, string> trackDictionary = new Dictionary<string, string>();
        private List<string> audioTracksList;
        private bool playMusic;
        private int trackIndex;
        private bool skip = false;
        private bool repeat = false;
        private object trackLock = new object();

        private int INPUT_AUDIO_LENGTH_MS = 40;
        private int INPUT_SAMPLE_RATE = 16000;
        private int SEGMENT_FRAMES;
        private WaveFormat format;
        private OpusEncoder encoder;

        // Events
        public event Action<byte[], DateTime> Broadcast;
        public event Action StopMusic;
        public event Action<int, string> TrackNameUpdate;
        public event Action<int, string> TrackTimerUpdate;
        public event Action<int, string> TrackNumberUpdate;

        public MusicController(int num)
        {
            stationNumber = num;

            SEGMENT_FRAMES = (INPUT_SAMPLE_RATE / 1000) * INPUT_AUDIO_LENGTH_MS;
            format = new WaveFormat(INPUT_SAMPLE_RATE, 16, 1);
            encoder = OpusEncoder.Create(INPUT_SAMPLE_RATE, 1, Application.Voip);
            encoder.ForwardErrorCorrection = false;
        }

        public void SetDirectory(string dir)
        {
            Logger.Debug($"Music directory set to path {dir} on station {stationNumber}");
            directory = dir;
        }

        public void Start()
        {
            Logger.Info($"Starting music playblack on station {stationNumber}");

            trackDictionary.Clear();
            int numTracks = readTracks();

            if (numTracks == 0)
            {
                // No playable tracks found
                ShowAudioTrackWarning();
                return;
            }

            audioTracksList = trackDictionary.Keys.ToList();
            shuffleAudioTracksList();

            skip = false;
            playMusic = true;
            Play();
        }

        public void Stop()
        {
            Logger.Info($"Stopping music playblack on station {stationNumber}");
            playMusic = false;
        }

        public void Play()
        {
            byte[] buffer = new byte[SEGMENT_FRAMES * 2];

            Task.Run(async () =>
            {
                while (playMusic)
                {
                    string trackPath = "";
                    string trackName = "";

                    try
                    {
                        trackPath = getNextTrack();
                        trackName = trackDictionary[trackPath];

                        Logger.Info($"Station {stationNumber} is now playing: {trackName}");

                        if (trackName.Length > 30)
                        {
                            trackName = trackName.Substring(0, 30) + "...";
                        }

                        // UI events for player
                        TrackNameUpdate(stationNumber, trackName);
                        TrackTimerUpdate(stationNumber, "00:00");
                        TrackNumberUpdate(stationNumber, getTrackNumLabel());

                        using (Mp3FileReader mp3 = new Mp3FileReader(trackPath))
                        {
                            using (var conversionStream = new WaveFormatConversionStream(format, mp3))
                            {
                                using (WaveStream pcm = WaveFormatConversionStream.CreatePcmStream(conversionStream))
                                {
                                    while (playMusic && (pcm.Read(buffer, 0, buffer.Length)) > 0)
                                    {
                                        Logger.Debug($"Music Controller current track time on station {stationNumber} is: {pcm.CurrentTime.ToString()}");

                                        if (skip)
                                        {
                                            // Don't skip to previous if more than 3 seconds into song
                                            if (pcm.CurrentTime >= new TimeSpan(0, 0, 3))
                                            {
                                                lock (trackLock)
                                                {
                                                    if (audioTracksList.IndexOf(trackPath) > trackIndex)
                                                    {
                                                        skip = true;
                                                        trackIndex = audioTracksList.IndexOf(trackPath);
                                                    }
                                                }
                                            }

                                            break;
                                        }

                                        new Thread (() => Stream(buffer, pcm.CurrentTime)).Start();
                                        await Task.Delay(INPUT_AUDIO_LENGTH_MS);
                                    }
                                }
                            }
                        }

                        lock (trackLock)
                        {
                            if (!skip && !repeat)
                            {
                                trackIndex += 1;
                            }
                        }
                    }

                    catch (Exception ex)
                    {
                        trackDictionary.Remove(trackPath);

                        if (trackDictionary.Count > 1)
                        {
                            Logger.Error(ex, $"Error encountered during audio playback of track: {trackPath}.  Error: ");
                            audioTracksList.RemoveAt(trackIndex);
                            ShowAudioTrackError(trackName);
                            continue;
                        }

                        else
                        {
                            Logger.Error(ex, $"Error encountered during audio playback on station: {stationNumber}.  Error: ");
                            StopMusic();
                        }
                    }
                }
            });
        }

        public void SkipForward()
        {
            lock(trackLock)
            {
                if (playMusic)
                {
                    Logger.Info($"Music Controller skip forward on station {stationNumber}");
                    skip = true;
                    trackIndex += 1;
                }
            }
        }

        public void SkipBackward()
        {
            lock (trackLock)
            {
                if (playMusic)
                {
                    Logger.Info($"Music Controller skip backward on station {stationNumber}");
                    skip = true;
                    trackIndex -= 1;
                }
            }
        }

        public void Repeat(bool value)
        {
            lock (trackLock)
            {
                Logger.Info($"Music Controller repeat enabled on station {stationNumber}");
                repeat = true;
            }
        }

        private async void Stream(byte[] buffer, TimeSpan currentTime)
        {
            Logger.Debug($"Music Controller current track time on station {stationNumber} is: {currentTime.ToString()}");

            // Encode as opus bytes
            int len;
            var buff = encoder.Encode(buffer, buffer.Length, out len);

            // Create copy with small buffer
            var encoded = new byte[len];
            Buffer.BlockCopy(buff, 0, encoded, 0, len);

            Broadcast(encoded, System.DateTime.Now);

            await Task.Run(() => timerTicked(currentTime));
        }

        private void timerTicked(TimeSpan time)
        {
            string timerTime = String.Format("{0:00}:{1:00}", time.Minutes, time.Seconds);

            TrackTimerUpdate(stationNumber, timerTime);
        }

        private int readTracks()
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    string[] files = Directory.GetFiles(directory, "*.mp3", SearchOption.TopDirectoryOnly);

                    foreach (string f in files)
                    {
                        string filename = Path.GetFileNameWithoutExtension(f);

                        trackDictionary.Add(f, filename);
                    }

                    return trackDictionary.Count;
                }

                else
                {
                    return 0;
                }
            }

            catch (IOException ex)
            {
                Logger.Warn(ex, "Unable to read tracks due to error: ");
                return 0;
            }
        }

        private void shuffleAudioTracksList()
        {
            var rand = new Random();
            List<string> shuffledList;

            // At end of playlist/queue - shuffle and guarantee no repeat
            if (audioTracksList.Count > 1)
            {
                string lastSong = audioTracksList[audioTracksList.Count - 1];
                audioTracksList = trackDictionary.Keys.ToList();
                shuffledList = audioTracksList.OrderBy(x => rand.Next()).ToList();

                while (shuffledList[0] == lastSong)
                {
                    shuffledList = audioTracksList.OrderBy(x => rand.Next()).ToList();
                }

                audioTracksList = shuffledList;

                lock (trackLock)
                {
                    trackIndex = 0;
                }
            }
        }

        private string getNextTrack()
        {
            try
            {
                if (trackIndex >= audioTracksList.Count)
                {
                    if (audioTracksList.Count > 1)
                    {
                        shuffleAudioTracksList();
                    }

                    else if (audioTracksList.Count == 1)
                    {
                        lock (trackLock)
                        {
                            trackIndex = 0;
                        }
                    }
                }

                lock (trackLock)
                {
                    if (skip)
                    {
                        skip = false;

                        if (trackIndex < 0)
                        {
                            trackIndex = 0;
                        }
                    }
                }

                return audioTracksList[trackIndex];
            }

            catch (Exception)
            {
                Logger.Warn($"Music Controller on station {stationNumber} unable to get next track.");
                return "";
            }
        }

        private string getTrackNumLabel()
        {
            int num = trackIndex + 1;

            return num.ToString() + " of " + audioTracksList.Count;
        }

        private void ShowAudioTrackWarning()
        {
            Logger.Warn("Audio track warning! No valid audio tracks were found.");

            StopMusic();

            MessageBox.Show($"No playable Audio Tracks were found. " +
                            $"\n\nPlease ensure tracks are of MP3 format, and placed in the selected directory.",
                            "No Tracks Found",
                            MessageBoxButton.OK,
                            MessageBoxImage.Exclamation);
        }

        private void ShowAudioTrackError(string trackName)
        {
            MessageBox.Show($"Error during track playback." +
                            $"\n\nTrack: {trackName}",
                            "Track Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
        }
    }
}

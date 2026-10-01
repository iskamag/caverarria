//! Device-free pull adapter for the original Organya and PixTone synthesizers.
//! Uses the upstream 10ms mixing buffers and message semantics; the host owns playback.
use super::*;

pub const SAMPLE_RATE: usize = 48000;

pub(super) struct PullMixer {
    rx: Receiver<PlaybackMessage>,
    bank: SoundBank,
    org: Box<OrgPlaybackEngine>,
    pixtone: Box<PixTonePlayback>,
    state: PlaybackState,
    saved_state: PlaybackStateType,
    bgm: Vec<u16>,
    sfx: Vec<u16>,
    bgm_index: usize,
    sfx_index: usize,
    samples: usize,
    speed: f32,
    music_volume: f32,
    saved_volume: f32,
    sfx_volume: f32,
    fade: bool,
    paused: bool,
}

impl PullMixer {
    pub(super) fn new(rx: Receiver<PlaybackMessage>, bank: SoundBank) -> Self {
        let mut org = Box::new(OrgPlaybackEngine::new());
        org.set_sample_rate(SAMPLE_RATE);
        org.loops = usize::MAX;
        let mut pixtone = Box::new(PixTonePlayback::new());
        pixtone.create_samples();
        let mut sfx = vec![0x8000; SAMPLE_RATE / 100];
        pixtone.mix(&mut sfx, SAMPLE_RATE as f32);
        Self {
            rx,
            bank,
            org,
            pixtone,
            state: PlaybackState::Stopped,
            saved_state: PlaybackStateType::None,
            bgm: vec![0x8000; SAMPLE_RATE / 100 * 2],
            sfx,
            bgm_index: 0,
            sfx_index: 0,
            samples: 0,
            speed: 1.0,
            music_volume: 1.0,
            saved_volume: 1.0,
            sfx_volume: 1.0,
            fade: false,
            paused: false,
        }
    }

    fn refill_music(&mut self) {
        self.bgm.fill(0x8000);
        self.samples = self.org.render_to(&mut self.bgm);
        self.bgm_index = 0;
    }

    fn cancel_fade(&mut self) {
        if self.fade {
            self.fade = false;
            self.music_volume = self.saved_volume;
        }
    }

    fn messages(&mut self) {
        while let Ok(message) = self.rx.try_recv() {
            match message {
                PlaybackMessage::PlayOrganyaSong(song) => {
                    if self.state == PlaybackState::Stopped {
                        self.saved_state = PlaybackStateType::None;
                    }
                    self.cancel_fade();
                    self.org.start_song(*song, &self.bank);
                    self.refill_music();
                    self.state = PlaybackState::PlayingOrg;
                }
                PlaybackMessage::PlaySample(id) => self.pixtone.play_sfx(id),
                PlaybackMessage::LoopSample(id) => self.pixtone.loop_sfx(id),
                PlaybackMessage::LoopSampleFreq(id, freq) => self.pixtone.loop_sfx_freq(id, freq),
                PlaybackMessage::StopSample(id) => self.pixtone.stop_sfx(id),
                PlaybackMessage::Stop => {
                    if self.state == PlaybackState::Stopped {
                        self.saved_state = PlaybackStateType::None;
                    }
                    self.state = PlaybackState::Stopped;
                }
                PlaybackMessage::SetSpeed(speed) => {
                    self.speed = speed;
                    self.org
                        .set_sample_rate((SAMPLE_RATE as f32 / speed) as usize);
                }
                PlaybackMessage::SetSongVolume(volume) => {
                    if self.fade {
                        self.saved_volume = volume;
                    } else {
                        self.music_volume = volume;
                    }
                }
                PlaybackMessage::SetSampleVolume(volume) => self.sfx_volume = volume,
                PlaybackMessage::FadeoutSong => {
                    self.fade = true;
                    self.saved_volume = self.music_volume;
                }
                PlaybackMessage::SaveState => {
                    self.saved_state = if self.state == PlaybackState::PlayingOrg {
                        PlaybackStateType::Organya(self.org.get_state())
                    } else {
                        PlaybackStateType::None
                    };
                }
                PlaybackMessage::RestoreState => match std::mem::take(&mut self.saved_state) {
                    PlaybackStateType::Organya(state) => {
                        self.org.set_state(state, &self.bank);
                        if self.state == PlaybackState::Stopped {
                            self.org.rewind();
                        }
                        self.refill_music();
                        self.cancel_fade();
                        self.state = PlaybackState::PlayingOrg;
                    }
                    _ => self.state = PlaybackState::Stopped,
                },
                PlaybackMessage::SetSampleParams(id, params) => {
                    self.pixtone.set_sample_parameters(id, params)
                }
                PlaybackMessage::SetOrgInterpolation(mode) => self.org.interpolation = mode,
                PlaybackMessage::SetSampleData(id, data) => self.pixtone.set_sample_data(id, data),
                #[cfg(feature = "ogg-playback")]
                PlaybackMessage::PlayOggSongSinglePart(_)
                | PlaybackMessage::PlayOggSongMultiPart(_, _) => {
                    // Portable Cave Story uses the original Organya score only.
                    self.state = PlaybackState::Stopped;
                }
            }
        }
    }

    pub(super) fn pause(&mut self, paused: bool) {
        self.paused = paused;
    }

    pub(super) fn disable(&mut self) {
        // First-use initialization can queue custom sample definitions before
        // the host chooses silent mode. Retain those definitions, but never
        // start a queued song or effect merely to discard it afterward.
        while let Ok(message) = self.rx.try_recv() {
            match message {
                PlaybackMessage::SetSampleParams(id, params) => {
                    self.pixtone.set_sample_parameters(id, params)
                }
                PlaybackMessage::SetSampleData(id, data) => self.pixtone.set_sample_data(id, data),
                PlaybackMessage::SetOrgInterpolation(mode) => self.org.interpolation = mode,
                PlaybackMessage::SetSpeed(speed) => {
                    self.speed = speed;
                    self.org.set_sample_rate((SAMPLE_RATE as f32 / speed) as usize);
                }
                PlaybackMessage::SetSongVolume(volume) => self.music_volume = volume,
                PlaybackMessage::SetSampleVolume(volume) => self.sfx_volume = volume,
                _ => {}
            }
        }
        self.pixtone.playback_state.clear();
        self.bgm.fill(0x8000);
        self.sfx.fill(0x8000);
        self.bgm_index = 0;
        self.sfx_index = 0;
        self.samples = 0;
        self.state = PlaybackState::Stopped;
        self.saved_state = PlaybackStateType::None;
        self.fade = false;
        self.paused = true;
    }

    pub(super) fn render(&mut self, output: &mut [i16]) {
        self.messages();
        if self.paused {
            output.fill(0);
            return;
        }
        for frame in output.chunks_exact_mut(2) {
            if self.fade {
                // Same five-second original fade, measured in emitted samples
                // rather than an operating-system clock or callback interval.
                self.music_volume = (self.music_volume
                    - self.saved_volume / (FADEOUT_DURATION * SAMPLE_RATE as f32))
                    .max(0.0);
            }
            let (left, right) = if self.state == PlaybackState::Stopped {
                (0x8000, 0x8000)
            } else {
                if self.bgm_index >= self.samples {
                    self.refill_music();
                }
                if self.samples < 2 {
                    (0x8000, 0x8000)
                } else {
                    let pair = (self.bgm[self.bgm_index], self.bgm[self.bgm_index + 1]);
                    self.bgm_index += 2;
                    pair
                }
            };
            let effect = self.sfx[self.sfx_index];
            self.sfx_index += 1;
            if self.sfx_index == self.sfx.len() {
                self.sfx_index = 0;
                self.sfx.fill(0x8000);
                self.pixtone
                    .mix(&mut self.sfx, SAMPLE_RATE as f32 / self.speed);
            }
            for (channel, sample) in frame.iter_mut().zip([left, right]) {
                *channel = clamp(
                    (((sample ^ 0x8000) as i16) as f32 * self.music_volume) as isize
                        + (((effect ^ 0x8000) as i16) as f32 * self.sfx_volume) as isize,
                    -0x7fff,
                    0x7fff,
                ) as i16;
            }
        }
    }
}

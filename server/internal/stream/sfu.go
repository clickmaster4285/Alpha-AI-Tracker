package stream

import (
	"encoding/json"
	"errors"
	"log"
	"sync"

	"github.com/pion/webrtc/v4"
)

// MediaMode selects the frame transport (JPEG remains the Phase-1 fallback).
type MediaMode string

const (
	MediaJPEG   MediaMode = "jpeg"
	MediaWebRTC MediaMode = "webrtc"
	MediaBoth   MediaMode = "both" // JPEG + WebRTC signaling in parallel
)

// ParseMediaMode normalizes LIVE_STREAM_MEDIA.
func ParseMediaMode(s string) MediaMode {
	switch MediaMode(s) {
	case MediaWebRTC:
		return MediaWebRTC
	case MediaBoth:
		return MediaBoth
	default:
		return MediaJPEG
	}
}

// SignalMessage is SDP/ICE JSON exchanged on push + watch sockets.
type SignalMessage struct {
	Type      string                     `json:"type"` // offer | answer | ice | media
	SDP       string                     `json:"sdp,omitempty"`
	Candidate *webrtc.ICECandidateInit   `json:"candidate,omitempty"`
	Role      string                     `json:"role,omitempty"` // publisher | subscriber
	Mode      string                     `json:"mode,omitempty"` // jpeg | webrtc | both
	WatcherID uint64                     `json:"watcherId,omitempty"`
}

// SignalSink writes one signaling message to a WebSocket peer.
type SignalSink func(msg SignalMessage)

var (
	ErrSFUDisabled = errors.New("webrtc sfu disabled")
	ErrNoPublisher = errors.New("no webrtc publisher")
)

// SFUConfig configures the in-process pion SFU.
type SFUConfig struct {
	Enabled    bool // true when media mode includes webrtc
	ICEServers []webrtc.ICEServer
}

// SFU is a per-employee room: one publisher (desktop) + N subscribers (admins).
// Media is never persisted — PeerConnections only.
type SFU struct {
	cfg SFUConfig
	api *webrtc.API

	mu    sync.Mutex
	rooms map[string]*sfuRoom
}

type sfuRoom struct {
	employeeID string
	pub        *peerState
	track      *webrtc.TrackLocalStaticRTP
	subs       map[uint64]*peerState
}

type peerState struct {
	pc   *webrtc.PeerConnection
	sink SignalSink
	role string
}

// NewSFU builds a pion API with default codecs + optional STUN/TURN.
func NewSFU(cfg SFUConfig) *SFU {
	if len(cfg.ICEServers) == 0 {
		cfg.ICEServers = []webrtc.ICEServer{{
			URLs: []string{"stun:stun.l.google.com:19302"},
		}}
	}
	m := &webrtc.MediaEngine{}
	if err := m.RegisterDefaultCodecs(); err != nil {
		log.Printf("[live-stream] sfu RegisterDefaultCodecs: %v", err)
	}
	api := webrtc.NewAPI(webrtc.WithMediaEngine(m))
	return &SFU{
		cfg:   cfg,
		api:   api,
		rooms: make(map[string]*sfuRoom),
	}
}

// Enabled reports whether WebRTC signaling should run.
func (s *SFU) Enabled() bool {
	return s != nil && s.cfg.Enabled
}

// Close tears down every room (server shutdown).
func (s *SFU) Close() {
	if s == nil {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	for id, r := range s.rooms {
		r.closeLocked()
		delete(s.rooms, id)
	}
}

func (s *SFU) getOrCreateRoomLocked(empID string) *sfuRoom {
	r, ok := s.rooms[empID]
	if ok {
		return r
	}
	r = &sfuRoom{
		employeeID: empID,
		subs:       make(map[uint64]*peerState),
	}
	s.rooms[empID] = r
	return r
}

// AttachPublisherSink registers the push-socket writer for ICE/answers.
func (s *SFU) AttachPublisherSink(empID string, sink SignalSink) {
	if !s.Enabled() {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	r := s.getOrCreateRoomLocked(empID)
	if r.pub == nil {
		r.pub = &peerState{role: "publisher", sink: sink}
	} else {
		r.pub.sink = sink
	}
}

// DetachPublisher closes the publisher PC when the push socket drops.
func (s *SFU) DetachPublisher(empID string) {
	if !s.Enabled() {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	r, ok := s.rooms[empID]
	if !ok || r.pub == nil {
		return
	}
	if r.pub.pc != nil {
		_ = r.pub.pc.Close()
		r.pub.pc = nil
	}
	r.track = nil
	// Subscribers stay; they will get no media until republish.
}

// AttachSubscriberSink registers the watch-socket writer for offers/ICE.
func (s *SFU) AttachSubscriberSink(empID string, watcherID uint64, sink SignalSink) {
	if !s.Enabled() {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	r := s.getOrCreateRoomLocked(empID)
	sub, ok := r.subs[watcherID]
	if !ok {
		sub = &peerState{role: "subscriber", sink: sink}
		r.subs[watcherID] = sub
	} else {
		sub.sink = sink
	}
	if r.track != nil {
		go s.offerToSubscriber(empID, watcherID)
	}
}

// DetachSubscriber closes one admin PeerConnection.
func (s *SFU) DetachSubscriber(empID string, watcherID uint64) {
	if !s.Enabled() {
		return
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	r, ok := s.rooms[empID]
	if !ok {
		return
	}
	if sub, ok := r.subs[watcherID]; ok {
		if sub.pc != nil {
			_ = sub.pc.Close()
		}
		delete(r.subs, watcherID)
	}
	if r.pub == nil && len(r.subs) == 0 {
		delete(s.rooms, empID)
	}
}

// HandlePublisherSignal processes offer/answer/ice from the desktop client.
func (s *SFU) HandlePublisherSignal(empID string, raw []byte) {
	if !s.Enabled() {
		return
	}
	var msg SignalMessage
	if err := json.Unmarshal(raw, &msg); err != nil {
		return
	}
	switch msg.Type {
	case "offer":
		if err := s.handlePublisherOffer(empID, msg.SDP); err != nil {
			log.Printf("[live-stream] sfu publisher offer employee=%s: %v", empID, err)
		}
	case "answer":
		_ = s.handlePublisherAnswer(empID, msg.SDP)
	case "ice":
		if msg.Candidate != nil {
			_ = s.addICE(empID, "publisher", 0, *msg.Candidate)
		}
	}
}

// HandleSubscriberSignal processes answer/ice from the web admin.
func (s *SFU) HandleSubscriberSignal(empID string, watcherID uint64, raw []byte) {
	if !s.Enabled() {
		return
	}
	var msg SignalMessage
	if err := json.Unmarshal(raw, &msg); err != nil {
		return
	}
	switch msg.Type {
	case "answer":
		if err := s.handleSubscriberAnswer(empID, watcherID, msg.SDP); err != nil {
			log.Printf("[live-stream] sfu subscriber answer employee=%s watcher=%d: %v", empID, watcherID, err)
		}
	case "ice":
		if msg.Candidate != nil {
			_ = s.addICE(empID, "subscriber", watcherID, *msg.Candidate)
		}
	}
}

func (s *SFU) pcConfig() webrtc.Configuration {
	return webrtc.Configuration{ICEServers: s.cfg.ICEServers}
}

func (s *SFU) handlePublisherOffer(empID, sdp string) error {
	s.mu.Lock()
	r := s.getOrCreateRoomLocked(empID)
	if r.pub == nil || r.pub.sink == nil {
		s.mu.Unlock()
		return errors.New("publisher sink not attached")
	}
	sink := r.pub.sink
	if r.pub.pc != nil {
		_ = r.pub.pc.Close()
		r.pub.pc = nil
		r.track = nil
	}
	s.mu.Unlock()

	pc, err := s.api.NewPeerConnection(s.pcConfig())
	if err != nil {
		return err
	}

	pc.OnICECandidate(func(c *webrtc.ICECandidate) {
		if c == nil {
			return
		}
		init := c.ToJSON()
		sink(SignalMessage{
			Type:      "ice",
			Candidate: &init,
			Role:      "publisher",
		})
	})

	pc.OnTrack(func(remote *webrtc.TrackRemote, _ *webrtc.RTPReceiver) {
		local, err := webrtc.NewTrackLocalStaticRTP(
			remote.Codec().RTPCodecCapability,
			remote.ID(),
			remote.StreamID(),
		)
		if err != nil {
			log.Printf("[live-stream] sfu NewTrackLocalStaticRTP: %v", err)
			return
		}
		s.mu.Lock()
		room, ok := s.rooms[empID]
		if !ok {
			s.mu.Unlock()
			return
		}
		room.track = local
		watcherIDs := make([]uint64, 0, len(room.subs))
		for id := range room.subs {
			watcherIDs = append(watcherIDs, id)
		}
		s.mu.Unlock()

		log.Printf("[live-stream] sfu publisher track employee=%s codec=%s", empID, remote.Codec().MimeType)
		for _, id := range watcherIDs {
			go s.offerToSubscriber(empID, id)
		}

		buf := make([]byte, 1500)
		for {
			n, _, readErr := remote.Read(buf)
			if readErr != nil {
				return
			}
			if _, writeErr := local.Write(buf[:n]); writeErr != nil {
				return
			}
		}
	})

	if err := pc.SetRemoteDescription(webrtc.SessionDescription{
		Type: webrtc.SDPTypeOffer,
		SDP:  sdp,
	}); err != nil {
		_ = pc.Close()
		return err
	}

	answer, err := pc.CreateAnswer(nil)
	if err != nil {
		_ = pc.Close()
		return err
	}
	gather := webrtc.GatheringCompletePromise(pc)
	if err := pc.SetLocalDescription(answer); err != nil {
		_ = pc.Close()
		return err
	}
	<-gather

	s.mu.Lock()
	r = s.getOrCreateRoomLocked(empID)
	if r.pub == nil {
		r.pub = &peerState{role: "publisher", sink: sink}
	}
	r.pub.pc = pc
	r.pub.sink = sink
	s.mu.Unlock()

	desc := pc.LocalDescription()
	sink(SignalMessage{
		Type: "answer",
		SDP:  desc.SDP,
		Role: "publisher",
	})
	return nil
}

func (s *SFU) handlePublisherAnswer(empID, sdp string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	r, ok := s.rooms[empID]
	if !ok || r.pub == nil || r.pub.pc == nil {
		return ErrNoPublisher
	}
	return r.pub.pc.SetRemoteDescription(webrtc.SessionDescription{
		Type: webrtc.SDPTypeAnswer,
		SDP:  sdp,
	})
}

func (s *SFU) handleSubscriberAnswer(empID string, watcherID uint64, sdp string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	r, ok := s.rooms[empID]
	if !ok {
		return ErrNoPublisher
	}
	sub, ok := r.subs[watcherID]
	if !ok || sub.pc == nil {
		return errors.New("subscriber pc missing")
	}
	return sub.pc.SetRemoteDescription(webrtc.SessionDescription{
		Type: webrtc.SDPTypeAnswer,
		SDP:  sdp,
	})
}

func (s *SFU) addICE(empID, role string, watcherID uint64, cand webrtc.ICECandidateInit) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	r, ok := s.rooms[empID]
	if !ok {
		return ErrNoPublisher
	}
	var pc *webrtc.PeerConnection
	if role == "publisher" {
		if r.pub != nil {
			pc = r.pub.pc
		}
	} else if sub, ok := r.subs[watcherID]; ok {
		pc = sub.pc
	}
	if pc == nil {
		return errors.New("peer connection missing for ice")
	}
	return pc.AddICECandidate(cand)
}

func (s *SFU) offerToSubscriber(empID string, watcherID uint64) {
	s.mu.Lock()
	r, ok := s.rooms[empID]
	if !ok || r.track == nil {
		s.mu.Unlock()
		return
	}
	sub, ok := r.subs[watcherID]
	if !ok || sub.sink == nil {
		s.mu.Unlock()
		return
	}
	track := r.track
	sink := sub.sink
	if sub.pc != nil {
		_ = sub.pc.Close()
		sub.pc = nil
	}
	s.mu.Unlock()

	pc, err := s.api.NewPeerConnection(s.pcConfig())
	if err != nil {
		log.Printf("[live-stream] sfu subscriber pc: %v", err)
		return
	}
	if _, err := pc.AddTrack(track); err != nil {
		log.Printf("[live-stream] sfu AddTrack: %v", err)
		_ = pc.Close()
		return
	}

	pc.OnICECandidate(func(c *webrtc.ICECandidate) {
		if c == nil {
			return
		}
		init := c.ToJSON()
		sink(SignalMessage{
			Type:      "ice",
			Candidate: &init,
			Role:      "subscriber",
			WatcherID: watcherID,
		})
	})

	offer, err := pc.CreateOffer(nil)
	if err != nil {
		_ = pc.Close()
		return
	}
	gather := webrtc.GatheringCompletePromise(pc)
	if err := pc.SetLocalDescription(offer); err != nil {
		_ = pc.Close()
		return
	}
	<-gather

	s.mu.Lock()
	r, ok = s.rooms[empID]
	if !ok {
		s.mu.Unlock()
		_ = pc.Close()
		return
	}
	sub, ok = r.subs[watcherID]
	if !ok {
		s.mu.Unlock()
		_ = pc.Close()
		return
	}
	sub.pc = pc
	sub.sink = sink
	s.mu.Unlock()

	desc := pc.LocalDescription()
	sink(SignalMessage{
		Type:      "offer",
		SDP:       desc.SDP,
		Role:      "subscriber",
		WatcherID: watcherID,
	})
}

func (r *sfuRoom) closeLocked() {
	if r.pub != nil && r.pub.pc != nil {
		_ = r.pub.pc.Close()
	}
	for _, sub := range r.subs {
		if sub.pc != nil {
			_ = sub.pc.Close()
		}
	}
	r.pub = nil
	r.track = nil
	r.subs = make(map[uint64]*peerState)
}

// ICEServerInfo is the JSON shape sent to desktop + web peers for RTCPeerConnection.
type ICEServerInfo struct {
	URLs       []string `json:"urls"`
	Username   string   `json:"username,omitempty"`
	Credential string   `json:"credential,omitempty"`
}

// ToPublicICEServers converts pion ICE servers into the wire DTO.
func ToPublicICEServers(servers []webrtc.ICEServer) []ICEServerInfo {
	out := make([]ICEServerInfo, 0, len(servers))
	for _, s := range servers {
		info := ICEServerInfo{
			URLs:     append([]string(nil), s.URLs...),
			Username: s.Username,
		}
		switch c := s.Credential.(type) {
		case string:
			info.Credential = c
		case []byte:
			info.Credential = string(c)
		}
		out = append(out, info)
	}
	if len(out) == 0 {
		out = []ICEServerInfo{{URLs: []string{"stun:stun.l.google.com:19302"}}}
	}
	return out
}

// ICEServers returns the configured ICE list (for status/start payloads).
func (s *SFU) ICEServers() []ICEServerInfo {
	if s == nil {
		return ToPublicICEServers(nil)
	}
	return ToPublicICEServers(s.cfg.ICEServers)
}

// ParseICEServers builds ICE server list from comma-separated URLs + optional TURN creds.
func ParseICEServers(urlsCSV, turnUser, turnPass string) []webrtc.ICEServer {
	var out []webrtc.ICEServer
	for _, u := range splitCSV(urlsCSV) {
		srv := webrtc.ICEServer{URLs: []string{u}}
		isTURN := len(u) >= 5 && u[:5] == "turn:"
		isTURNS := len(u) >= 6 && u[:6] == "turns:"
		if turnUser != "" && (isTURN || isTURNS) {
			srv.Username = turnUser
			srv.Credential = turnPass
		}
		out = append(out, srv)
	}
	if len(out) == 0 {
		out = []webrtc.ICEServer{{URLs: []string{"stun:stun.l.google.com:19302"}}}
	}
	return out
}

func splitCSV(s string) []string {
	var parts []string
	start := 0
	for i := 0; i <= len(s); i++ {
		if i == len(s) || s[i] == ',' {
			p := trimSpace(s[start:i])
			if p != "" {
				parts = append(parts, p)
			}
			start = i + 1
		}
	}
	return parts
}

func trimSpace(s string) string {
	i, j := 0, len(s)
	for i < j && (s[i] == ' ' || s[i] == '\t') {
		i++
	}
	for j > i && (s[j-1] == ' ' || s[j-1] == '\t') {
		j--
	}
	return s[i:j]
}

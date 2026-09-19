package stream

import (
	"testing"
)

func TestParseMediaMode(t *testing.T) {
	if ParseMediaMode("webrtc") != MediaWebRTC {
		t.Fatal("expected webrtc")
	}
	if ParseMediaMode("both") != MediaBoth {
		t.Fatal("expected both")
	}
	if ParseMediaMode("") != MediaJPEG {
		t.Fatal("expected jpeg default")
	}
}

func TestSFUPublisherSubscriberSignaling(t *testing.T) {
	sfu := NewSFU(SFUConfig{Enabled: true})
	defer sfu.Close()

	pubMsgs := make(chan SignalMessage, 8)
	subMsgs := make(chan SignalMessage, 8)

	sfu.AttachPublisherSink("EMP-1", func(msg SignalMessage) {
		pubMsgs <- msg
	})
	sfu.AttachSubscriberSink("EMP-1", 1, func(msg SignalMessage) {
		subMsgs <- msg
	})

	// Without a real browser offer, rooms should still attach cleanly.
	sfu.DetachSubscriber("EMP-1", 1)
	sfu.DetachPublisher("EMP-1")
}

func TestParseICEServers(t *testing.T) {
	srvs := ParseICEServers("stun:stun.example:3478, turn:turn.example:3478", "u", "p")
	if len(srvs) != 2 {
		t.Fatalf("got %d servers", len(srvs))
	}
	if srvs[1].Username != "u" || srvs[1].Credential != "p" {
		t.Fatalf("TURN creds not applied: %+v", srvs[1])
	}
}

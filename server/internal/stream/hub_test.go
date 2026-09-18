package stream

import (
	"sync"
	"testing"
	"time"
)

func testHub() *Hub {
	cfg := DefaultConfig()
	cfg.IdleSec = 2
	return NewHub(cfg)
}

func TestPushSubscribeOrdering(t *testing.T) {
	h := testHub()
	defer h.Close()

	id, ch, err := h.Subscribe("EMP-1")
	if err != nil {
		t.Fatal(err)
	}
	defer h.Unsubscribe("EMP-1", id)

	ctrl, ok := h.RegisterClient("EMP-1")
	if !ok {
		t.Fatal("register failed")
	}
	select {
	case ev := <-ctrl:
		if ev.Type != "start" {
			t.Fatalf("want start, got %s", ev.Type)
		}
	case <-time.After(time.Second):
		t.Fatal("expected start after register with existing watcher")
	}

	jpeg := []byte{0xff, 0xd8, 0xff, 0xd9} // minimal JPEG-ish
	if err := h.PushFrame("EMP-1", jpeg); err != nil {
		t.Fatal(err)
	}

	select {
	case fr := <-ch:
		if len(fr.JPEG) != len(jpeg) || fr.Seq != 1 {
			t.Fatalf("bad frame: seq=%d len=%d", fr.Seq, len(fr.JPEG))
		}
	case <-time.After(time.Second):
		t.Fatal("expected frame")
	}
}

func TestStopClearsMailbox(t *testing.T) {
	h := testHub()
	defer h.Close()

	id, _, err := h.Subscribe("EMP-2")
	if err != nil {
		t.Fatal(err)
	}
	ctrl, _ := h.RegisterClient("EMP-2")
	<-ctrl // start

	_ = h.PushFrame("EMP-2", []byte("frame-a"))
	snap := h.Snapshot("EMP-2")
	if !snap.Streaming || snap.Seq != 1 {
		t.Fatalf("expected streaming seq=1, got %+v", snap)
	}

	h.Unsubscribe("EMP-2", id)
	select {
	case ev := <-ctrl:
		if ev.Type != "stop" {
			t.Fatalf("want stop, got %s", ev.Type)
		}
	case <-time.After(time.Second):
		t.Fatal("expected stop")
	}

	snap = h.Snapshot("EMP-2")
	if snap.Wanted || snap.Streaming || snap.Seq != 1 {
		// seq may remain; streaming/wanted must be false; frame cleared so Streaming false
	}
	if snap.Wanted || snap.Streaming {
		t.Fatalf("expected stopped snapshot, got %+v", snap)
	}
}

func TestMultiWatcherFanOut(t *testing.T) {
	h := testHub()
	defer h.Close()

	id1, ch1, err := h.Subscribe("EMP-3")
	if err != nil {
		t.Fatal(err)
	}
	defer h.Unsubscribe("EMP-3", id1)
	id2, ch2, err := h.Subscribe("EMP-3")
	if err != nil {
		t.Fatal(err)
	}
	defer h.Unsubscribe("EMP-3", id2)

	h.RegisterClient("EMP-3")
	jpeg := []byte("hello-jpeg")
	if err := h.PushFrame("EMP-3", jpeg); err != nil {
		t.Fatal(err)
	}

	got := 0
	deadline := time.After(time.Second)
	for got < 2 {
		select {
		case <-ch1:
			got++
			ch1 = nil
		case <-ch2:
			got++
			ch2 = nil
		case <-deadline:
			t.Fatalf("fan-out incomplete, got %d", got)
		}
	}
}

func TestConcurrentPushSubscribe(t *testing.T) {
	h := testHub()
	defer h.Close()

	var wg sync.WaitGroup
	for i := 0; i < 20; i++ {
		wg.Add(1)
		go func(n int) {
			defer wg.Done()
			emp := "EMP-C"
			id, ch, err := h.Subscribe(emp)
			if err != nil {
				return
			}
			defer h.Unsubscribe(emp, id)
			h.RegisterClient(emp)
			_ = h.PushFrame(emp, []byte{byte(n)})
			select {
			case <-ch:
			case <-time.After(200 * time.Millisecond):
			}
		}(i)
	}
	wg.Wait()
}

func TestWatcherCap(t *testing.T) {
	cfg := DefaultConfig()
	cfg.MaxWatchersPerEmployee = 2
	h := NewHub(cfg)
	defer h.Close()

	id1, _, err := h.Subscribe("EMP-CAP")
	if err != nil {
		t.Fatal(err)
	}
	defer h.Unsubscribe("EMP-CAP", id1)
	id2, _, err := h.Subscribe("EMP-CAP")
	if err != nil {
		t.Fatal(err)
	}
	defer h.Unsubscribe("EMP-CAP", id2)

	_, _, err = h.Subscribe("EMP-CAP")
	if err != ErrTooManyWatchers {
		t.Fatalf("want ErrTooManyWatchers, got %v", err)
	}
}

func TestFrameTooLarge(t *testing.T) {
	cfg := DefaultConfig()
	cfg.FrameMaxBytes = 4
	h := NewHub(cfg)
	defer h.Close()

	id, _, _ := h.Subscribe("EMP-BIG")
	defer h.Unsubscribe("EMP-BIG", id)
	h.RegisterClient("EMP-BIG")

	err := h.PushFrame("EMP-BIG", []byte("12345"))
	if err != ErrFrameTooLarge {
		t.Fatalf("want ErrFrameTooLarge, got %v", err)
	}
}

func TestPushWithoutWatchersRejected(t *testing.T) {
	h := testHub()
	defer h.Close()
	h.RegisterClient("EMP-X")
	if err := h.PushFrame("EMP-X", []byte("x")); err != ErrNotWanted {
		t.Fatalf("want ErrNotWanted, got %v", err)
	}
}

func TestCapability(t *testing.T) {
	h := testHub()
	defer h.Close()
	h.SetCapability("EMP-W", Capability{Platform: "windows", StreamAvailable: true, Version: "1.0.0"})
	cap, ok := h.CapabilityOf("EMP-W")
	if !ok || !cap.StreamAvailable || cap.Platform != "windows" {
		t.Fatalf("bad capability: ok=%v %+v", ok, cap)
	}
}

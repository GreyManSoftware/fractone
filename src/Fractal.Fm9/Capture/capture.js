// Injected into the page by MediaCapturer (idempotent). Finds the page's main media element, reports what stands in the
// way (bot checks, sign-in walls, consent dialogs, DRM, ads), and records the element's own audio through Web Audio while
// it plays inside the requested window and no ad is showing. It never solves, skips or hides anything: the capture side
// stops and reports instead. The only thing it clicks is a consent dialog's decline button.
(() => {
	if (window.__fractone) return 'ready';

	const deep = (selector) => {
		const found = [];
		const walk = (root) => {
			root.querySelectorAll(selector).forEach((element) => found.push(element));
			root.querySelectorAll('*').forEach((element) => { if (element.shadowRoot) walk(element.shadowRoot); });
		};
		walk(document);
		return found;
	};
	const visible = (element) => {
		const box = element.getBoundingClientRect();
		const style = getComputedStyle(element);
		return box.width > 1 && box.height > 1 && style.visibility !== 'hidden' && style.display !== 'none' && style.opacity !== '0';
	};
	const label = (element) => (element.innerText || element.value || element.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ');
	const pageText = () => (document.body ? document.body.innerText : '').slice(0, 30000);

	const botCheck = /verify (that )?you('|’)?re (a )?human|verify you are (a )?human|are you a robot|i('|’)?m not a robot|unusual traffic|confirm you('|’)?re not a bot|complete the security check|checking your browser|press (and|&) hold/i;
	const signIn = /sign in to (confirm|continue|watch|view|listen)|log ?in to (continue|watch|view|listen)|this (video|content) is (private|unavailable)|members[- ]only|age[- ]restricted|confirm your age|subscribe to (watch|listen)/i;
	const consentText = /cookie|consent|your privacy|personal data|gdpr|we and our partners/i;
	const decline = /^(reject|decline|refuse|deny)( all)?( cookies| non-essential( cookies)?| optional( cookies)?| additional cookies)?$|^(use |accept |allow )?(only )?(strictly )?(necessary|essential|required)( cookies)?( only)?$|^continue without accepting$|^alle ablehnen$|^tout refuser$|^rechazar todo$|^rifiuta tutto$|^alles weigeren$|^recusar tudo$/i;
	const challengeFrame = /captcha|challenges?\.|turnstile|arkoselabs|funcaptcha|perimeterx|datadome/i;
	const adClass = /(^|[\s_-])(ad-showing|ad-interrupting|ad-playing|ads-playing|flag-ads|ad-active)([\s_-]|$)/i;
	const adText = /^(skip ads?|advertisement|ad \d+ of \d+|ad ·|sponsored)(\b|$)/i;

	const media = () => {
		const videos = deep('video').filter((element) => element.readyState > 0 || element.currentSrc || element.src);
		const shown = videos.filter(visible).sort((a, b) => {
			const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
			return rb.width * rb.height - ra.width * ra.height;
		});
		return shown[0] || videos[0] || deep('audio').find((element) => element.currentSrc || element.src) || null;
	};

	const adActive = (element) => {
		let node = element;
		for (let depth = 0; node && depth < 8; depth++, node = node.parentElement || (node.getRootNode && node.getRootNode().host)) {
			if (typeof node.className === 'string' && adClass.test(node.className)) return true;
		}
		const scope = element && element.closest ? (element.parentElement && element.parentElement.parentElement) || document : document;
		for (const candidate of scope.querySelectorAll('button, [role=button], span, div')) {
			if (candidate.childElementCount > 2) continue;
			const text = label(candidate);
			if (text.length < 40 && adText.test(text) && visible(candidate)) return true;
		}
		return false;
	};

	const declineButtons = () => deep('button, [role=button], a, input[type=button], input[type=submit]').filter((button) => visible(button) && decline.test(label(button)));

	// A consent dialog: a modal about cookies or consent, or a decline button inside consent wording (dialogs are often
	// custom elements with no dialog role).
	const consentDialog = () => {
		const modal = deep('[role=dialog], [aria-modal=true], dialog').find((element) => visible(element) && consentText.test(label(element).slice(0, 3000)));
		if (modal) return modal;
		for (const button of declineButtons()) {
			let node = button;
			for (let depth = 0; node && depth < 12; depth++, node = node.parentElement || (node.getRootNode && node.getRootNode().host)) {
				if (consentText.test(label(node).slice(0, 3000))) return node;
			}
		}
		return null;
	};

	// A page's own play button, for players that create their media element only when it is pressed.
	const playButton = () => deep('button, [role=button], a, div, span').filter((element) => {
		if (!visible(element)) return false;
		const name = (element.getAttribute('aria-label') || element.getAttribute('title') || element.innerText || '').trim();
		return /^play( video| audio| media)?$/i.test(name) || (typeof element.className === 'string' && /(^|[\s_-])(big-)?play-?button([\s_-]|$)/i.test(element.className));
	}).sort((a, b) => {
		const ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
		return rb.width * rb.height - ra.width * ra.height;
	})[0] || null;

	let state = null;

	window.__fractone = {
		inspect() {
			const text = pageText();
			const frames = deep('iframe').filter((frame) => visible(frame) && challengeFrame.test(frame.src || frame.title || ''));
			const element = media();
			const dialog = consentDialog();
			return {
				title: document.title,
				botCheck: frames.length > 0 || botCheck.test(text),
				signIn: signIn.test(text),
				consent: !!dialog || /consent\./i.test(location.hostname),
				media: element ? {
					kind: element.tagName.toLowerCase(), duration: element.duration, readyState: element.readyState,
					encrypted: !!element.mediaKeys, error: element.error ? element.error.code : null
				} : null,
				ad: element ? adActive(element) : false
			};
		},

		decline() {
			const choice = declineButtons()[0];
			if (!choice) return null;
			const text = label(choice);
			choice.click();
			return text;
		},

		play() {
			const button = playButton();
			if (!button) return null;
			button.click();
			return (button.getAttribute('aria-label') || button.getAttribute('title') || button.innerText || 'play').trim();
		},

		async start(from, to) {
			const element = media();
			if (!element) throw new Error('no media element');
			const wait = (event, ms) => new Promise((resolve) => {
				const done = (ok) => { element.removeEventListener(event, yes); element.removeEventListener('error', no); clearTimeout(timer); resolve(ok); };
				const yes = () => done(true), no = () => done(false);
				const timer = setTimeout(() => done(false), ms);
				element.addEventListener(event, yes); element.addEventListener('error', no);
			});
			const notes = [];
			// Cross-origin media without CORS reaches Web Audio as silence; ask the server for CORS (a normal request).
			const source = element.currentSrc || element.src || '';
			if (/^https?:/i.test(source) && new URL(source, location.href).origin !== location.origin && element.crossOrigin === null) {
				element.crossOrigin = 'anonymous';
				element.src = source;
				element.load();
				if (!(await wait('loadedmetadata', 15000))) {
					element.removeAttribute('crossorigin');
					element.src = source;
					element.load();
					notes.push('cors-refused');
				}
			}
			if (element.readyState < 1 && !(await wait('loadedmetadata', 20000))) throw new Error('the media did not load');
			if (!isFinite(element.duration)) notes.push('live');
			const context = new AudioContext();
			const node = context.createMediaElementSource(element);
			const processor = context.createScriptProcessor(4096, 2, 2);
			node.connect(processor);
			processor.connect(context.destination);
			state = { context, element, processor, left: [], right: [], frames: 0, peak: 0, adSeen: false, inAd: false, ended: false, from, to, notes };
			processor.onaudioprocess = (event) => {
				for (let channel = 0; channel < event.outputBuffer.numberOfChannels; channel++) event.outputBuffer.getChannelData(channel).fill(0);
				const ad = adActive(element);
				if (ad) state.adSeen = true;
				// When an ad gives way to the programme, which players restart or resume, go to the part.
				if (state.inAd && !ad && element.currentTime < from - 0.5) element.currentTime = from;
				state.inAd = ad;
				const time = element.currentTime;
				if (!ad && (time >= to || element.ended)) state.ended = true;
				if (ad || element.paused || element.seeking || time < from || time >= to) return;
				const left = event.inputBuffer.getChannelData(0);
				const right = event.inputBuffer.numberOfChannels > 1 ? event.inputBuffer.getChannelData(1) : left;
				state.left.push(new Float32Array(left));
				state.right.push(new Float32Array(right));
				state.frames += left.length;
				for (let index = 0; index < left.length; index++) {
					const value = Math.max(Math.abs(left[index]), Math.abs(right[index]));
					if (value > state.peak) state.peak = value;
				}
			};
			element.muted = false;
			element.volume = 1;
			element.playbackRate = 1;
			// Never seek inside an ad: the programme is sought to the part when the ad ends.
			if (adActive(element)) state.inAd = true;
			else element.currentTime = from;
			await context.resume();
			await element.play();
			return { sampleRate: context.sampleRate, notes };
		},

		// Hands over what was recorded since the last call: interleaved stereo float32, base64.
		drain() {
			if (!state) return null;
			const frames = state.left.reduce((sum, chunk) => sum + chunk.length, 0);
			const interleaved = new Float32Array(frames * 2);
			let offset = 0;
			for (let chunk = 0; chunk < state.left.length; chunk++) {
				const left = state.left[chunk], right = state.right[chunk];
				for (let index = 0; index < left.length; index++, offset += 2) {
					interleaved[offset] = left[index];
					interleaved[offset + 1] = right[index];
				}
			}
			state.left = [];
			state.right = [];
			const bytes = new Uint8Array(interleaved.buffer);
			let binary = '';
			for (let index = 0; index < bytes.length; index += 0x8000) binary += String.fromCharCode.apply(null, bytes.subarray(index, index + 0x8000));
			const element = state.element;
			return {
				data: btoa(binary), frames: state.frames, peak: state.peak, ended: state.ended, adSeen: state.adSeen,
				ad: adActive(element), time: element.currentTime, paused: element.paused, error: element.error ? element.error.code : null
			};
		},

		resume() {
			if (state && state.element.paused && !state.ended) state.element.play().catch(() => {});
		},

		stop() {
			if (!state) return;
			state.element.pause();
			state.processor.onaudioprocess = null;
			state.context.close();
		}
	};
	return 'ready';
})();

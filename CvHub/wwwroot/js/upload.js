// Drag-and-drop image upload straight to Cloudinary (unsigned preset) or placeholder fallback.
window.cvUpload = {
    init(dotnetRef, inputId) {
        const input = document.getElementById(inputId);
        if (!input) return;
        input.addEventListener('change', async () => {
            const file = input.files && input.files[0];
            if (file) await window.cvUpload.upload(dotnetRef, file);
            input.value = '';
        });
    },
    wireDrop(zoneId, dotnetRef) {
        const zone = document.getElementById(zoneId);
        if (!zone) return;
        zone.addEventListener('dragover', e => { e.preventDefault(); });
        zone.addEventListener('dragenter', () => dotnetRef.invokeMethodAsync('Dragging', true));
        zone.addEventListener('dragleave', () => dotnetRef.invokeMethodAsync('Dragging', false));
        zone.addEventListener('drop', async e => {
            e.preventDefault();
            dotnetRef.invokeMethodAsync('Dragging', false);
            const file = e.dataTransfer?.files?.[0];
            if (file && file.type.startsWith('image/')) await window.cvUpload.upload(dotnetRef, file);
        });
    },
    async upload(dotnetRef, file) {
        await dotnetRef.invokeMethodAsync('Uploading');
        const cloudName = document.documentElement.dataset.cloudName || '';
        const preset = document.documentElement.dataset.uploadPreset || '';
        try {
            if (cloudName && preset) {
                const fd = new FormData();
                fd.append('file', file);
                fd.append('upload_preset', preset);
                const resp = await fetch(`https://api.cloudinary.com/v1_1/${cloudName}/image/upload`, { method: 'POST', body: fd });
                const json = await resp.json();
                await dotnetRef.invokeMethodAsync('Uploaded', json.secure_url || null);
            } else {
                // Demo fallback: deterministic external placeholder (no bytes touch our server or DB).
                const seed = encodeURIComponent(file.name.replace(/\.[^.]+$/, '') || 'photo');
                await dotnetRef.invokeMethodAsync('Uploaded', `https://placehold.co/400x400/6366f1/ffffff?text=${seed}`);
            }
        } catch {
            await dotnetRef.invokeMethodAsync('Uploaded', null);
        }
    }
};

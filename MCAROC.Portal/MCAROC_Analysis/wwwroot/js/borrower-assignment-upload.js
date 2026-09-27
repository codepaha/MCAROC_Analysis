(() => {
    const form = document.getElementById('assignmentUploadForm');
    if (!form) return;
    const file = document.getElementById('requestFile');
    const status = document.getElementById('assignmentUploadStatus');
    form.addEventListener('paste', event => {
        const image = [...(event.clipboardData?.items || [])].find(item => item.type.startsWith('image/'));
        if (!image) return;
        const source = image.getAsFile();
        if (!source) return;
        event.preventDefault();
        const transfer = new DataTransfer();
        transfer.items.add(new File([source], source.type === 'image/jpeg' ? 'request-screenshot.jpg' : 'request-screenshot.png', { type: source.type }));
        file.files = transfer.files;
        status.textContent = 'Screenshot attached.';
    });
    form.addEventListener('submit', event => {
        if (!file.files.length && !document.getElementById('requestText').value.trim()) {
            event.preventDefault();
            status.textContent = 'Upload a request or paste its text.';
            return;
        }
        if (file.files[0]?.size > 10 * 1024 * 1024) {
            event.preventDefault();
            status.textContent = 'Use a document up to 10 MB.';
            return;
        }
        document.getElementById('createAssignmentButton').disabled = true;
        status.textContent = 'Saving assignment?';
    });
})();

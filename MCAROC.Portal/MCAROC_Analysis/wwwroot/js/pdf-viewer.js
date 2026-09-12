/**
 * In-App PDF Viewer Client Controller
 * Uses Mozilla PDF.js v6.3.289
 */
import * as pdfjsLib from '/lib/pdfjs/pdf.mjs';
import { parsePageFragment, clampPage } from '/js/pdf-viewer-core.js';

// Configure worker
pdfjsLib.GlobalWorkerOptions.workerSrc = '/lib/pdfjs/pdf.worker.mjs';

class DocumentViewer {
  constructor() {
    this.container = document.getElementById('pdfViewerContainer');
    if (!this.container) return;

    this.pdfUrl = this.container.dataset.pdfUrl;
    this.downloadUrl = this.container.dataset.downloadUrl;

    this.pdfDoc = null;
    this.currentPage = 1;
    this.currentScale = 1.25;
    this.pageRendering = false;
    this.pageNumPending = null;

    // UI Elements
    this.canvas = document.getElementById('pdfCanvas');
    this.ctx = this.canvas ? this.canvas.getContext('2d') : null;
    this.textLayerDiv = document.getElementById('textLayer');
    this.pageNumberInput = document.getElementById('pageNumberInput');
    this.pageCountLabel = document.getElementById('pageCountLabel');
    this.prevBtn = document.getElementById('prevPageBtn');
    this.nextBtn = document.getElementById('nextPageBtn');
    this.zoomInBtn = document.getElementById('zoomInBtn');
    this.zoomOutBtn = document.getElementById('zoomOutBtn');
    this.zoomSelect = document.getElementById('zoomSelect');
    this.warningBanner = document.getElementById('pageWarningBanner');
    this.loadingSpinner = document.getElementById('pdfLoadingSpinner');
    this.errorContainer = document.getElementById('pdfErrorContainer');
    this.errorMessage = document.getElementById('pdfErrorMessage');
    this.statusAnnouncer = document.getElementById('pdfStatusAnnouncer');

    this.init();
  }

  async init() {
    this.bindEvents();

    try {
      this.showLoading(true);
      const loadingTask = pdfjsLib.getDocument({
        url: this.pdfUrl,
        cMapUrl: '/lib/pdfjs/cmaps/',
        cMapPacked: true,
        standardFontDataUrl: '/lib/pdfjs/standard_fonts/',
        wasmUrl: '/lib/pdfjs/wasm/',
        rangeChunkSize: 65536
      });

      this.pdfDoc = await loadingTask.promise;
      this.showLoading(false);

      if (this.pageCountLabel) {
        this.pageCountLabel.textContent = ` / ${this.pdfDoc.numPages}`;
      }
      if (this.pageNumberInput) {
        this.pageNumberInput.max = this.pdfDoc.numPages;
      }

      // Initial page from URL hash
      const requested = parsePageFragment(window.location.hash);
      const clampResult = clampPage(requested, this.pdfDoc.numPages);
      this.currentPage = clampResult.page;

      if (clampResult.isClamped && clampResult.message) {
        this.showWarning(clampResult.message);
      }

      await this.renderPage(this.currentPage);
    } catch (err) {
      this.showLoading(false);
      this.showError('Unable to render PDF document. You can still download the file using the button above.', err);
    }
  }

  bindEvents() {
    if (this.prevBtn) {
      this.prevBtn.addEventListener('click', () => this.navigatePage(-1));
    }
    if (this.nextBtn) {
      this.nextBtn.addEventListener('click', () => this.navigatePage(1));
    }

    if (this.pageNumberInput) {
      this.pageNumberInput.addEventListener('change', (e) => {
        const val = parseInt(e.target.value, 10);
        if (!isNaN(val)) {
          this.goToPage(val, true);
        }
      });
      this.pageNumberInput.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          this.pageNumberInput.blur();
        }
      });
    }

    if (this.zoomInBtn) {
      this.zoomInBtn.addEventListener('click', () => this.adjustZoom(0.2));
    }
    if (this.zoomOutBtn) {
      this.zoomOutBtn.addEventListener('click', () => this.adjustZoom(-0.2));
    }
    if (this.zoomSelect) {
      this.zoomSelect.addEventListener('change', (e) => {
        const scaleVal = parseFloat(e.target.value);
        if (!isNaN(scaleVal)) {
          this.currentScale = scaleVal;
          this.queueRenderPage(this.currentPage);
        }
      });
    }

    // Keyboard navigation
    window.addEventListener('keydown', (e) => {
      // Don't trigger when typing in inputs
      if (['INPUT', 'TEXTAREA', 'SELECT'].includes(document.activeElement?.tagName)) {
        return;
      }
      if (e.key === 'ArrowLeft' || e.key === 'PageUp') {
        e.preventDefault();
        this.navigatePage(-1);
      } else if (e.key === 'ArrowRight' || e.key === 'PageDown') {
        e.preventDefault();
        this.navigatePage(1);
      }
    });

    // Hash change navigation
    window.addEventListener('hashchange', () => {
      if (!this.pdfDoc) return;
      const requested = parsePageFragment(window.location.hash);
      const clampResult = clampPage(requested, this.pdfDoc.numPages);
      if (clampResult.isClamped && clampResult.message) {
        this.showWarning(clampResult.message);
      } else {
        this.hideWarning();
      }
      if (clampResult.page !== this.currentPage) {
        this.goToPage(clampResult.page, false);
      }
    });
  }

  showLoading(isLoading) {
    if (this.loadingSpinner) {
      this.loadingSpinner.style.display = isLoading ? 'flex' : 'none';
    }
  }

  showWarning(msg) {
    if (this.warningBanner) {
      this.warningBanner.textContent = msg;
      this.warningBanner.style.display = 'block';
    }
  }

  hideWarning() {
    if (this.warningBanner) {
      this.warningBanner.textContent = '';
      this.warningBanner.style.display = 'none';
    }
  }

  showError(msg, err) {
    console.error('PDF Viewer Error:', err);
    if (this.errorContainer) {
      if (this.errorMessage) {
        this.errorMessage.textContent = msg;
      }
      this.errorContainer.style.display = 'block';
    }
    if (this.statusAnnouncer) {
      this.statusAnnouncer.textContent = `Error: ${msg}`;
    }
  }

  announceStatus(msg) {
    if (this.statusAnnouncer) {
      this.statusAnnouncer.textContent = msg;
    }
  }

  adjustZoom(delta) {
    let newScale = Math.round((this.currentScale + delta) * 10) / 10;
    if (newScale < 0.5) newScale = 0.5;
    if (newScale > 3.0) newScale = 3.0;
    this.currentScale = newScale;
    if (this.zoomSelect) {
      this.zoomSelect.value = newScale.toString();
    }
    this.queueRenderPage(this.currentPage);
  }

  navigatePage(offset) {
    if (!this.pdfDoc) return;
    this.goToPage(this.currentPage + offset, true);
  }

  goToPage(pageNum, updateHash = true) {
    if (!this.pdfDoc) return;

    const clampResult = clampPage(pageNum, this.pdfDoc.numPages);
    if (clampResult.isClamped && clampResult.message) {
      this.showWarning(clampResult.message);
    } else {
      this.hideWarning();
    }

    this.currentPage = clampResult.page;
    if (updateHash) {
      // Update hash without triggering a jump
      history.replaceState(null, '', `#page=${this.currentPage}`);
    }

    this.queueRenderPage(this.currentPage);
  }

  queueRenderPage(num) {
    if (this.pageRendering) {
      this.pageNumPending = num;
    } else {
      this.renderPage(num);
    }
  }

  async renderPage(num) {
    this.pageRendering = true;
    this.announceStatus(`Rendering page ${num} of ${this.pdfDoc.numPages}`);

    try {
      const page = await this.pdfDoc.getPage(num);
      const viewport = page.getViewport({ scale: this.currentScale });

      // Support high-DPI displays
      const outputScale = window.devicePixelRatio || 1;

      this.canvas.width = Math.floor(viewport.width * outputScale);
      this.canvas.height = Math.floor(viewport.height * outputScale);
      this.canvas.style.width = Math.floor(viewport.width) + 'px';
      this.canvas.style.height = Math.floor(viewport.height) + 'px';

      const transform = outputScale !== 1 ? [outputScale, 0, 0, outputScale, 0, 0] : null;

      const renderContext = {
        canvasContext: this.ctx,
        transform: transform,
        viewport: viewport
      };

      await page.render(renderContext).promise;

      // Render accessible text layer for selection and search
      if (this.textLayerDiv) {
        this.textLayerDiv.innerHTML = '';
        this.textLayerDiv.style.width = Math.floor(viewport.width) + 'px';
        this.textLayerDiv.style.height = Math.floor(viewport.height) + 'px';

        const textContent = await page.getTextContent();
        const textLayer = new pdfjsLib.TextLayer({
          textContentSource: textContent,
          container: this.textLayerDiv,
          viewport: viewport
        });
        await textLayer.render();
      }

      // Update toolbar UI
      if (this.pageNumberInput) {
        this.pageNumberInput.value = num;
      }
      if (this.prevBtn) {
        this.prevBtn.disabled = num <= 1;
      }
      if (this.nextBtn) {
        this.nextBtn.disabled = num >= this.pdfDoc.numPages;
      }

      this.announceStatus(`Page ${num} of ${this.pdfDoc.numPages} loaded.`);
    } catch (err) {
      console.error(`Failed to render page ${num}:`, err);
      this.announceStatus(`Failed to render page ${num}.`);
    } finally {
      this.pageRendering = false;
      if (this.pageNumPending !== null) {
        const pending = this.pageNumPending;
        this.pageNumPending = null;
        this.renderPage(pending);
      }
    }
  }
}

// Bootstrap once DOM is ready
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', () => new DocumentViewer());
} else {
  new DocumentViewer();
}

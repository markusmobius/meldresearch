import { TestBed } from '@angular/core/testing';
import { MeldInstall } from './meld-install';

describe('Install card', () => {
  async function render() {
    const fixture = TestBed.createComponent(MeldInstall);
    await fixture.whenStable();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }
  const click = (element: HTMLElement, text: string) =>
    [...element.querySelectorAll('button')].find(button => button.textContent?.includes(text))!.click();

  it('offers one-line installers bound to this server', async () => {
    const { fixture, element } = await render();
    fixture.componentInstance.os.set('windows');
    await fixture.whenStable();
    expect(element.querySelector('code')!.textContent).toBe(`irm -useb ${location.origin}/api/install/windows | iex`);
    click(element, 'macOS');
    await fixture.whenStable();
    expect(element.querySelector('h3')!.textContent).toContain('Meld Research for macOS');
    expect(element.querySelector('code')!.textContent).toBe(`curl -fsSL ${location.origin}/api/install/macos | bash`);
  });

  it('links the downloadable script in the manual tab', async () => {
    const { fixture, element } = await render();
    fixture.componentInstance.os.set('windows');
    click(element, 'Manual');
    await fixture.whenStable();
    const download = element.querySelector<HTMLAnchorElement>('a.download-button')!;
    expect(download.getAttribute('href')).toBe('/api/install/windows');
    expect(element.querySelector('code')!.textContent).toContain('meldresearch-install.ps1');
  });

  it('copies the command and confirms it', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    const { fixture, element } = await render();
    element.querySelector<HTMLButtonElement>('.copy-button')!.click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenCalledWith(fixture.componentInstance.os() === 'mac'
      ? fixture.componentInstance.macCommand : fixture.componentInstance.windowsCommand);
    expect(element.querySelector('.copy-button mat-icon')!.textContent).toBe('check');
  });
});

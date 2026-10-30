import { TestBed } from '@angular/core/testing';
import { MeldToolbar } from './meld-toolbar';

describe('Remote account menu', () => {
  afterEach(() => document.querySelectorAll('meta[name="meld-account"], form#meld-sign-out').forEach(node => node.remove()));

  it('is absent without a signed-in session', async () => {
    const fixture = TestBed.createComponent(MeldToolbar);
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('.remote-account-btn')).toBeNull();
  });

  it('shows the signed-in account and signs out through the page form', async () => {
    const meta = document.createElement('meta');
    meta.name = 'meld-account';
    meta.content = 'Ada Lovelace';
    meta.dataset['email'] = 'ada@example.invalid';
    document.head.append(meta);
    const form = document.createElement('form');
    form.id = 'meld-sign-out';
    const submit = vi.spyOn(form, 'submit').mockImplementation(() => undefined);
    document.body.append(form);

    const fixture = TestBed.createComponent(MeldToolbar);
    await fixture.whenStable();
    const button = (fixture.nativeElement as HTMLElement).querySelector('.remote-account-btn')!;
    expect(button.textContent).toContain('A');
    expect(button.textContent).toContain('Ada Lovelace');
    fixture.componentInstance.signOut();
    expect(submit).toHaveBeenCalled();
  });
});
